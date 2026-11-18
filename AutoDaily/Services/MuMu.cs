using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoDaily.Models;
using Microsoft.Win32;

namespace AutoDaily.Services;

/// <summary>MuMu 多開器裡的一台模擬器（對應 MuMuManager 的 -v 編號）。</summary>
public sealed record MuMuPlayer(int Index, string Name, bool ProcessStarted, bool AndroidStarted, string State, string LaunchError)
{
    /// <summary>Android 已經開機完成，自動化程式可以連線了。</summary>
    public bool Ready => AndroidStarted && (State.Length == 0 || State == "start_finished");

    public string StateText => Ready ? "執行中" : ProcessStarted ? "開機中" : "未啟動";
}

/// <summary>
/// 透過 MuMu 內建的 MuMuManager.exe 命令列開關模擬器、查詢開機狀態，
/// 讓手機遊戲的自動化程式啟動時模擬器已經開好（不必先開多開器再手動按啟動）。
/// </summary>
public static class MuMu
{
    public const string ManagerExe = "MuMuManager.exe";

    public static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(5);

    /// <summary>開機完成後再等一下，讓桌面與服務都起來。</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>MuMu 5（nx_main）與 MuMu 12（shell）放 MuMuManager.exe 的位置。</summary>
    private static readonly string[] ManagerRelativePaths = { @"nx_main\" + ManagerExe, @"shell\" + ManagerExe, ManagerExe };

    // ───── 尋找 MuMuManager.exe ─────

    /// <summary>使用者填的位置可以是 MuMuManager.exe、MuMu 資料夾或裡面任何一個 exe；空白就自動尋找。</summary>
    public static string? ResolveManager(string? configured)
    {
        var path = TaskRunner.CleanPath(configured);
        if (path.Length == 0) return FindManager();
        if (File.Exists(path) && Path.GetFileName(path).Equals(ManagerExe, StringComparison.OrdinalIgnoreCase)) return path;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return SearchUpwards(dir);
    }

    /// <summary>從安裝資訊、常見路徑與正在執行的模擬器找出 MuMuManager.exe。</summary>
    public static string? FindManager()
    {
        var roots = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var keyPath in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var uninstall = hive.OpenSubKey(keyPath);
                if (uninstall == null) continue;
                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(sub);
                    if (app?.GetValue("DisplayName") is not string name || !name.Contains("MuMu", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (app.GetValue("InstallLocation") is string location && location.Trim('"').Length > 0)
                        roots.Add(location.Trim('"'));
                    if (app.GetValue("UninstallString") is string uninstaller && Path.GetDirectoryName(uninstaller.Trim('"')) is { Length: > 0 } dir)
                        roots.Add(dir);
                }
            }
            catch
            {
                // 讀不到登錄檔就改用其他方式
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        roots.Add(Path.Combine(programFiles, "Netease", "MuMuPlayer"));
        roots.Add(Path.Combine(programFiles, "Netease", "MuMuPlayer-12.0"));
        roots.Add(Path.Combine(programFiles, "Netease", "MuMu Player 12"));

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var rel in ManagerRelativePaths)
        {
            var candidate = Path.Combine(root, rel);
            if (File.Exists(candidate)) return candidate;
        }

        // 最後從正在執行的模擬器程式往上找
        foreach (var processName in new[] { "MuMuNxDevice", "MuMuNxMain", "MuMuPlayer" })
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                string? file = null;
                try
                {
                    file = p.MainModule?.FileName;
                }
                catch
                {
                    // 沒有權限讀取
                }
                if (SearchUpwards(Path.GetDirectoryName(file)) is { } found) return found;
            }
        }
        return null;
    }

    private static string? SearchUpwards(string? dir)
    {
        for (var i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir))
        {
            foreach (var rel in ManagerRelativePaths)
            {
                var candidate = Path.Combine(dir, rel);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // ───── 查詢 ─────

    public static async Task<(List<MuMuPlayer> Players, string? Error)> QueryAllAsync(string manager, CancellationToken ct = default)
    {
        var (code, output) = await RunAsync(manager, "info -v all", TimeSpan.FromSeconds(20), ct);
        return Parse(output, code);
    }

    public static async Task<(MuMuPlayer? Player, string? Error)> QueryAsync(string manager, int index, CancellationToken ct = default)
    {
        var (code, output) = await RunAsync(manager, $"info -v {index}", TimeSpan.FromSeconds(20), ct);
        var (players, error) = Parse(output, code);
        var player = players.FirstOrDefault(p => p.Index == index);
        return (player, player == null ? error ?? "MuMuManager 沒有回傳這台模擬器的資料" : null);
    }

    /// <summary>解讀 MuMuManager 的 JSON：單台是一個物件，-v all 是以編號為鍵的物件，錯誤時有 errcode / errmsg。</summary>
    internal static (List<MuMuPlayer> Players, string? Error) Parse(string output, int exitCode)
    {
        var list = new List<MuMuPlayer>();
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            var text = output.Trim();
            return (list, text.Length > 0 ? Shorten(text) : $"MuMuManager 沒有回應（代碼 {exitCode}）");
        }

        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.TryGetProperty("errcode", out var errCode) && errCode.ValueKind == JsonValueKind.Number && errCode.GetInt32() != 0)
            {
                var message = root.TryGetProperty("errmsg", out var m) ? m.GetString() : null;
                return (list, string.IsNullOrWhiteSpace(message) ? $"錯誤代碼 {errCode.GetInt32()}" : message);
            }

            if (root.TryGetProperty("index", out _))
            {
                if (ToPlayer(root) is { } single) list.Add(single);
            }
            else
            {
                foreach (var prop in root.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Object && ToPlayer(prop.Value) is { } player)
                        list.Add(player);
            }
        }
        catch (JsonException ex)
        {
            return (list, "無法解讀 MuMuManager 的回應：" + ex.Message);
        }
        return (list.OrderBy(p => p.Index).ToList(), null);
    }

    private static MuMuPlayer? ToPlayer(JsonElement e)
    {
        if (!e.TryGetProperty("index", out var idx)) return null;
        var index = idx.ValueKind switch
        {
            JsonValueKind.Number => idx.GetInt32(),
            JsonValueKind.String when int.TryParse(idx.GetString(), out var n) => n,
            _ => -1,
        };
        if (index < 0) return null;

        string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool Flag(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var display = Text("name");
        return new MuMuPlayer(index, display.Length > 0 ? display : $"模擬器 {index}",
            Flag("is_process_started"), Flag("is_android_started"), Text("player_state"), Text("launch_err_msg"));
    }

    // ───── 開啟／關閉 ─────

    /// <summary>
    /// 確保模擬器已開機完成；必要時啟動它並等待。成功傳回 null，失敗傳回要回報的結果。
    /// <paramref name="tick"/> 會收到已等待的時間，用來更新畫面上的計時。
    /// </summary>
    public static async Task<RunOutcome?> EnsureRunningAsync(GameTask task, Action<TimeSpan>? tick, CancellationToken ct)
    {
        var manager = ResolveManager(task.EmulatorPath);
        if (manager == null)
            return new(RunState.Failed, "找不到 MuMu 模擬器的 MuMuManager.exe，請在任務設定的「模擬器」填入它的位置");

        var index = task.EmulatorIndex;
        var (player, error) = await QueryAsync(manager, index, ct);
        if (player == null)
        {
            var notFound = error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;
            return new(RunState.Failed,
                notFound ? $"MuMu 裡沒有編號 {index} 的模擬器，請在任務設定重新選擇" : $"無法查詢 MuMu 模擬器 #{index}：{error}",
                Retryable: !notFound);
        }

        if (player.Ready)
        {
            Logger.Write($"　MuMu 模擬器 #{index}（{player.Name}）已經開著");
            return null;
        }

        Logger.Write($"　開啟 MuMu 模擬器 #{index}（{player.Name}），等待 Android 開機完成…");
        var clock = Stopwatch.StartNew();
        var launch = RunAsync(manager, $"control -v {index} launch", TimeSpan.FromMinutes(2), ct);
        string? launchError = null;
        var launchChecked = false;

        while (clock.Elapsed < BootTimeout)
        {
            await Task.Delay(PollInterval, ct);
            tick?.Invoke(clock.Elapsed);

            if (!launchChecked && launch.IsCompleted)
            {
                launchChecked = true;
                var (code, output) = await launch;
                launchError = CommandError(code, output);
                if (launchError != null) Logger.Write($"　MuMuManager 回報：{launchError}", LogLevel.Warn);
            }

            (player, _) = await QueryAsync(manager, index, ct);
            if (player?.Ready == true)
            {
                Logger.Write($"　模擬器開機完成（{(int)clock.Elapsed.TotalSeconds} 秒）");
                await Task.Delay(SettleDelay, ct);
                tick?.Invoke(clock.Elapsed);
                return null;
            }

            // 啟動指令直接失敗、模擬器也沒開起來，就不用等到逾時
            if (launchError != null && player?.ProcessStarted != true && clock.Elapsed > TimeSpan.FromSeconds(30))
                return new(RunState.Failed, $"MuMu 模擬器 #{index} 無法啟動：{launchError}", Retryable: true);
        }

        var detail = player?.LaunchError is { Length: > 0 } e ? $"（{e}）" : "";
        Logger.Write($"　模擬器 {(int)BootTimeout.TotalMinutes} 分鐘內沒有開機完成{detail}，先把它關掉", LogLevel.Warn);
        await ShutdownAsync(manager, index);
        return new(RunState.Failed, $"MuMu 模擬器 {(int)BootTimeout.TotalMinutes} 分鐘內沒有開機完成{detail}", Retryable: true);
    }

    /// <summary>正常關閉這個任務設定的模擬器（不是強制結束程式）。</summary>
    public static async Task ShutdownAsync(GameTask task)
    {
        if (ResolveManager(task.EmulatorPath) is { } manager)
            await ShutdownAsync(manager, task.EmulatorIndex);
    }

    private static async Task ShutdownAsync(string manager, int index)
    {
        var (player, _) = await QueryAsync(manager, index);
        if (player is { ProcessStarted: false }) return;

        var (code, output) = await RunAsync(manager, $"control -v {index} shutdown", TimeSpan.FromSeconds(30), CancellationToken.None);
        if (CommandError(code, output) is { } error)
        {
            Logger.Write($"　關閉 MuMu 模擬器 #{index} 失敗：{error}", LogLevel.Warn);
            return;
        }

        // 等它真的關掉，免得下一個遊戲啟動時搶資源
        for (var i = 0; i < 15; i++)
        {
            (player, _) = await QueryAsync(manager, index);
            if (player is not { ProcessStarted: true }) break;
            await Task.Delay(PollInterval);
        }
        Logger.Write($"　已關閉 MuMu 模擬器 #{index}");
    }

    // ───── 執行 MuMuManager ─────

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return (-1, $"無法執行 {Path.GetFileName(exe)}：{ex.Message}");
        }
        if (p == null) return (-1, $"無法執行 {Path.GetFileName(exe)}");

        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已經結束
                }
                ct.ThrowIfCancellationRequested();
                return (-1, $"{Path.GetFileName(exe)} {(int)timeout.TotalSeconds} 秒內沒有回應");
            }
            return (p.ExitCode, await stdout + await stderr);
        }
    }

    /// <summary>launch / shutdown 這類指令：回傳 errcode 不是 0 或程式異常結束才算失敗（沒有輸出不算）。</summary>
    private static string? CommandError(int code, string output)
    {
        if (output.Contains("errcode")) return Parse(output, code).Error;
        return code == 0 ? null : $"代碼 {code}" + (output.Trim().Length > 0 ? $"：{Shorten(output.Trim())}" : "");
    }

    private static string Shorten(string text) => text.Length > 120 ? text[..120] + "…" : text;
}
