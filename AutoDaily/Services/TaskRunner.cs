using System.ComponentModel;
using System.Diagnostics;
using AutoDaily.Models;

namespace AutoDaily.Services;

/// <summary>一次執行的結果；Retryable 表示值得再跑一次。</summary>
public sealed record RunOutcome(RunState State, string Message, bool Retryable = false);

public static class TaskRunner
{
    /// <summary>主程式在這個時間內就結束，多半是參數錯誤或程式已經開著。</summary>
    private static readonly TimeSpan SuspiciouslyFast = TimeSpan.FromSeconds(20);

    /// <summary>記錄檔出現未完成訊息後，再安靜這麼久就不等了（例如 ok-ww 出錯後會一直開著）。</summary>
    private static readonly TimeSpan QuietAfterFailure = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan LogPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>執行任務；記錄檔顯示沒完成、卡住或異常結束時，依設定自動重跑。</summary>
    public static async Task<RunOutcome> RunWithRetriesAsync(GameTask task, AppSettings settings, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var retries = Math.Max(0, task.MaxRetries);
        for (var attempt = 0; ; attempt++)
        {
            task.Attempt = attempt;
            if (attempt > 0) Logger.Write($"↻ {task.Name}：第 {attempt} 次重試");

            var outcome = await RunOnceAsync(task, settings, ct);
            if (!outcome.Retryable || attempt >= retries || ct.IsCancellationRequested)
            {
                task.Elapsed = total.Elapsed;
                if (attempt > 0 && outcome.State == RunState.Success)
                    return outcome with { Message = $"{outcome.Message}（重試 {attempt} 次後成功）" };
                if (attempt > 0 && outcome.Retryable)
                    return outcome with { Message = $"{outcome.Message}（已重試 {attempt} 次）" };
                return outcome;
            }

            task.Message = outcome.Message;
            Logger.Write($"　{outcome.Message}，{(int)RetryDelay.TotalSeconds} 秒後重新執行", LogLevel.Warn);
            try
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                task.Elapsed = total.Elapsed;
                return new RunOutcome(RunState.Stopped, "已由使用者中止");
            }
        }
    }

    public static async Task<RunOutcome> RunOnceAsync(GameTask task, AppSettings settings, CancellationToken ct)
    {
        var exe = CleanPath(task.ExePath);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return new(RunState.Failed, string.IsNullOrWhiteSpace(exe) ? "尚未設定程式路徑" : $"找不到程式：{exe}");

        var toolDir = Path.GetDirectoryName(exe)!;
        var workDir = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? toolDir : CleanPath(task.WorkingDirectory);
        var games = ParseNames(task.GameProcesses);
        var ignore = games.Concat(ParseNames(settings.IgnoreProcesses)).ToList();

        // 手機遊戲先把模擬器開好；工具若已經開著先關掉（否則新開的只會把指令轉給舊的就結束）
        var prep = Stopwatch.StartNew();
        try
        {
            if (task.StartEmulator && await MuMu.EnsureRunningAsync(task, waited => task.Elapsed = waited, ct) is { } emulatorFailure)
                return emulatorFailure;
            await CloseRunningCopiesAsync(exe, ct);
        }
        catch (OperationCanceledException)
        {
            return new(RunState.Stopped, "已由使用者中止");
        }
        var prepTime = prep.Elapsed;

        // 先記下記錄檔目前的位置，之後只看這次新寫入的內容
        var logs = LogMonitor.Create(task.LogPaths, task.SuccessMarkers, task.FailureMarkers, toolDir);
        logs.Begin();

        Process? process;
        try
        {
            process = Start(exe, task.Arguments, workDir);
        }
        catch (Exception ex)
        {
            return new(RunState.Failed, $"無法啟動：{ex.Message}");
        }
        if (process == null) return new(RunState.Failed, "無法啟動程式");

        Logger.Write($"　啟動 {Path.GetFileName(exe)} {task.Arguments}".TrimEnd());

        using (process)
        using (var watcher = new ProcessTreeWatcher(process, Path.GetFileName(exe), ignore, task.TrackChildren))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            watcher.ChildDiscovered += name =>
            {
                if (seen.Add(name)) Logger.Write($"　追蹤子程式：{name}");
            };

            var clock = Stopwatch.StartNew();
            var timeout = task.TimeoutMinutes > 0 ? TimeSpan.FromMinutes(task.TimeoutMinutes) : Timeout.InfiniteTimeSpan;
            var stallLimit = task.LogStallMinutes > 0 ? TimeSpan.FromMinutes(task.LogStallMinutes) : Timeout.InfiniteTimeSpan;
            var lastLogPoll = TimeSpan.Zero;
            var failureReported = false;

            // 工具開了卻沒開始跑（例如 MaaEnd 沒設定自動執行的配置）時，遊戲永遠不會出現，
            // 與其等到逾時，不如在幾分鐘內就判定失敗、換下一個。
            var gameStartLimit = TimeSpan.FromMinutes(task.GameStartTimeoutMinutes);
            var gameSeen = games.Count == 0 || task.GameStartTimeoutMinutes <= 0;
            // 卡住偵測從遊戲開起來才開始算：之前可能在更新或下載，記錄檔不一定會動
            var gameSeenAt = TimeSpan.Zero;

            var cursorKeeper = task.KeepCursorInGame == true && games.Count > 0 ? new CursorKeeper(games) : null;
            var lastCursorCheck = TimeSpan.Zero;

            try
            {
                while (true)
                {
                    watcher.Poll();
                    task.Elapsed = prepTime + clock.Elapsed;
                    if (!watcher.AnyActive) break;

                    if (!gameSeen)
                    {
                        if (watcher.FindRunning(games) is { } gameName)
                        {
                            gameSeen = true;
                            gameSeenAt = clock.Elapsed;
                            Logger.Write($"　偵測到遊戲已啟動：{gameName}");
                        }
                        else if (clock.Elapsed > gameStartLimit)
                        {
                            Logger.Write($"　{task.GameStartTimeoutMinutes} 分鐘內都沒有偵測到遊戲啟動，判定工具沒有開始執行任務", LogLevel.Warn);
                            await Task.Run(watcher.KillTools);
                            return new(RunState.Failed,
                                $"{task.GameStartTimeoutMinutes} 分鐘內遊戲沒有啟動，工具可能沒有開始執行任務（請檢查該工具的自動執行設定）",
                                Retryable: true);
                        }
                    }

                    if (cursorKeeper != null && gameSeen && clock.Elapsed - lastCursorCheck >= TimeSpan.FromSeconds(1))
                    {
                        lastCursorCheck = clock.Elapsed;
                        if (cursorKeeper.Tick() && cursorKeeper.MoveCount == 1)
                            Logger.Write("　滑鼠游標在遊戲視窗外，已移回遊戲中央（之後跑出去也會自動移回）");
                    }

                    if (logs.Configured && clock.Elapsed - lastLogPoll >= LogPollInterval)
                    {
                        lastLogPoll = clock.Elapsed;
                        logs.Poll();

                        if (logs.FailureLine != null && !failureReported)
                        {
                            failureReported = true;
                            Logger.Write($"　記錄檔出現未完成訊息：{logs.FailureLine}", LogLevel.Warn);
                        }

                        string? reason = null;
                        if (stallLimit != Timeout.InfiniteTimeSpan && gameSeen && logs.AnyFileSeen
                            && logs.IdleFor > stallLimit && clock.Elapsed - gameSeenAt > stallLimit)
                            reason = $"記錄檔已 {task.LogStallMinutes} 分鐘沒有更新，判定卡住";
                        else if (logs.FailureLine != null && logs.IdleFor > QuietAfterFailure)
                            reason = $"出現未完成訊息後已 {(int)QuietAfterFailure.TotalMinutes} 分鐘沒有動靜（{logs.FailureLine}）";

                        if (reason != null)
                        {
                            Logger.Write($"　{reason}，關閉工具", LogLevel.Warn);
                            await Task.Run(watcher.KillTools);
                            if (task.KillGameAfter) await CloseGamesAsync(task, games);
                            task.Elapsed = prepTime + clock.Elapsed;
                            return new(RunState.Incomplete, reason, Retryable: true);
                        }
                    }

                    if (timeout != Timeout.InfiniteTimeSpan && clock.Elapsed > timeout)
                    {
                        Logger.Write($"　超過 {task.TimeoutMinutes} 分鐘仍未結束（{string.Join(", ", watcher.ActiveNames)}），強制關閉", LogLevel.Warn);
                        await Task.Run(watcher.KillTools);
                        if (task.KillGameAfter) await CloseGamesAsync(task, games);
                        return new(RunState.Timeout, $"超過 {task.TimeoutMinutes} 分鐘未完成，已強制結束");
                    }

                    // 剛啟動時輪詢快一點，才抓得到「開完就關」的啟動器所產生的子程式
                    await Task.Delay(clock.Elapsed < TimeSpan.FromSeconds(30) ? 250 : 1000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Write("　正在關閉自動化程式…", LogLevel.Warn);
                await Task.Run(watcher.KillTools);
                return new(RunState.Stopped, "已由使用者中止");
            }

            task.Elapsed = prepTime + clock.Elapsed;
            var exitCode = watcher.RootExitCode;
            if (cursorKeeper is { MoveCount: > 1 } keeper)
                Logger.Write($"　這次共把游標移回遊戲視窗 {keeper.MoveCount} 次");

            if (task.KillGameAfter)
            {
                // 給工具一點時間自己正常關閉遊戲
                await Task.Delay(3000);
                await CloseGamesAsync(task, games);
            }

            if (logs.Configured)
            {
                logs.Finish();
                if (!logs.AnyFileSeen)
                    return new(RunState.Warning, "程式已結束，但找不到記錄檔，無法確認是否完成（請檢查「記錄檔位置」）");
                if (logs.FailureLine != null)
                    return new(RunState.Incomplete, $"記錄檔顯示沒完成：{logs.FailureLine}", Retryable: true);
                if (logs.HasSuccessMarkers && logs.SuccessLine == null)
                    return new(RunState.Incomplete, "程式結束了，但記錄檔沒有出現完成訊息", Retryable: true);
                if (logs.HasSuccessMarkers)
                    return new(RunState.Success, "完成（已從記錄檔確認）");
            }

            if (exitCode is int code && code != 0)
                return new(RunState.Warning, $"程式結束（代碼 {code}）", Retryable: true);
            if (clock.Elapsed < SuspiciouslyFast)
                return new(RunState.Warning,
                    $"程式在 {(int)clock.Elapsed.TotalSeconds} 秒內就結束了，請確認參數是否正確，或該程式是否已經開著",
                    Retryable: true);
            return new(RunState.Success, "完成");
        }
    }

    private static Process? Start(string exe, string? arguments, string workDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = arguments ?? "",
            WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        };
        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // 需要提升權限（本程式不是以管理員執行時）
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            return Process.Start(psi);
        }
    }

    private static async Task CloseGamesAsync(GameTask task, IReadOnlyCollection<string> games)
    {
        // 模擬器用 MuMu 自己的關機指令，不要直接砍程式
        if (task.StartEmulator)
        {
            await MuMu.ShutdownAsync(task);
            return;
        }
        if (games.Count == 0) return;
        var killed = await Task.Run(() => KillByNames(games));
        if (killed > 0) Logger.Write($"　已關閉遊戲（{killed} 個程式）");
    }

    /// <summary>
    /// 同一個工具已經開著時（例如手動測試後沒關），再開一個通常只會把指令轉給舊的視窗就立刻結束，
    /// 本程式就等不到它跑完。所以先請舊的關閉，10 秒內沒關再強制結束。系統程式（cmd 等）不處理。
    /// </summary>
    private static async Task CloseRunningCopiesAsync(string exe, CancellationToken ct)
    {
        var full = Path.GetFullPath(exe);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || (windows.Length > 0 && full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return;

        var copies = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full))
            .Where(p => IsSameExecutable(p, full))
            .ToList();
        if (copies.Count == 0) return;

        Logger.Write($"　{Path.GetFileName(full)} 已經開著，先關閉它再重新啟動（否則新開的不會執行任務）", LogLevel.Warn);
        try
        {
            foreach (var p in copies)
            {
                try
                {
                    p.CloseMainWindow();
                }
                catch
                {
                    // 沒有視窗或已經結束
                }
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && copies.Any(p => !HasExited(p)))
                await Task.Delay(500, ct);

            foreach (var p in copies.Where(p => !HasExited(p)))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch
                {
                    // 已經結束或無權限
                }
            }
            // 讓它釋放「只能開一個」的鎖
            await Task.Delay(1000, ct);
        }
        finally
        {
            foreach (var p in copies) p.Dispose();
        }
    }

    private static bool IsSameExecutable(Process p, string fullPath)
    {
        try
        {
            return string.Equals(p.MainModule?.FileName, fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch
        {
            return true;
        }
    }

    public static int KillByNames(IEnumerable<string> names)
    {
        var count = 0;
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    count++;
                }
                catch
                {
                    // 已經結束或無權限
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return count;
    }

    public static List<string> ParseNames(string? text) =>
        (text ?? "")
        .Split(new[] { ';', ',', '；', '，', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeExeName)
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string NormalizeExeName(string name)
    {
        name = Path.GetFileName(name.Trim().Trim('"'));
        if (name.Length == 0) return name;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public static string CleanPath(string? path) =>
        Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
}
