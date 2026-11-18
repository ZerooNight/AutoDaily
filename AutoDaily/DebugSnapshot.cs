#if DEBUG
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoDaily.Models;
using AutoDaily.ViewModels;
using AutoDaily.Views;

namespace AutoDaily;

/// <summary>
/// 開發用：把畫面輸出成 PNG 以便檢查版面。
/// AutoDaily.exe --snapshot out.png [--view main|edit|settings|confirm] [--demo]
/// </summary>
internal static class DebugSnapshot
{
    public static bool TryRun(string[] args)
    {
        var t = Array.IndexOf(args, "--test-runner");
        if (t >= 0 && t + 1 < args.Length)
        {
            var report = Task.Run(RunnerSelfTest).GetAwaiter().GetResult();
            File.WriteAllText(args[t + 1], report);
            Environment.Exit(0);
        }

        var i = Array.IndexOf(args, "--snapshot");
        if (i < 0 || i + 1 >= args.Length) return false;
        var path = args[i + 1];
        var v = Array.IndexOf(args, "--view");
        var view = v >= 0 && v + 1 < args.Length ? args[v + 1] : "main";

        var vm = new MainViewModel();
        if (args.Contains("--demo")) vm.ApplyDemoState();

        var editTask = vm.Tasks[0].Clone();
        var pr = Array.IndexOf(args, "--preset");
        if (pr >= 0 && pr + 1 < args.Length && Services.Presets.All.FirstOrDefault(p => p.Key == args[pr + 1]) is { } preset)
            Services.Presets.Apply(editTask, preset);
        var ex = Array.IndexOf(args, "--exe");
        if (ex >= 0 && ex + 1 < args.Length) editTask.ExePath = args[ex + 1];

        Window window = view switch
        {
            "edit" => new TaskEditWindow(editTask, isNew: false),
            "settings" => new SettingsWindow(vm.Settings.Clone()),
            _ => new MainWindow(vm),
        };
        if (args.Contains("--full")) window.MaxHeight = 4000;
        var h = Array.IndexOf(args, "--height");
        if (h >= 0 && h + 1 < args.Length && double.TryParse(args[h + 1], out var height))
        {
            window.MaxHeight = Math.Max(window.MaxHeight, height);
            window.Height = height;
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(700));
        var s = Array.IndexOf(args, "--scroll");
        if ((args.Contains("--scroll-end") || s >= 0) && FindChild<System.Windows.Controls.ScrollViewer>(window) is { } scroller)
        {
            var fraction = s >= 0 && s + 1 < args.Length && double.TryParse(args[s + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 1.0;
            scroller.ScrollToVerticalOffset(scroller.ScrollableHeight * fraction);
            Pump(TimeSpan.FromMilliseconds(300));
        }

        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var fs = File.Create(path)) encoder.Save(fs);

        // 直接結束：示範狀態是「執行中」，正常關閉會跳出確認視窗
        Environment.Exit(0);
        return true;
    }

    /// <summary>用 cmd / powershell / ping 模擬各種工具行為，驗證「何時算完成」的判斷。</summary>
    private static async Task<string> RunnerSelfTest()
    {
        var sb = new System.Text.StringBuilder();
        Services.Logger.Logged += e => { lock (sb) sb.AppendLine($"    log[{e.Level}] {e.Message}"); };
        var settings = new AppSettings();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        async Task Case(string name, string args, string games, bool killGame, int timeoutMin, Func<RunState, double, bool> expect,
            int cancelAfterMs = 0, int gameStartMin = 0)
        {
            var task = new GameTask
            {
                Name = name, ExePath = cmd, Arguments = args, GameProcesses = games, KillGameAfter = killGame,
                TimeoutMinutes = timeoutMin, GameStartTimeoutMinutes = gameStartMin,
            };
            using var cts = new CancellationTokenSource();
            if (cancelAfterMs > 0) cts.CancelAfter(cancelAfterMs);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (state, msg, _) = await Services.TaskRunner.RunOnceAsync(task, settings, cts.Token);
            var secs = sw.Elapsed.TotalSeconds;
            var pingLeft = System.Diagnostics.Process.GetProcessesByName("PING").Length;
            lock (sb) sb.AppendLine($"{(expect(state, secs) ? "PASS" : "FAIL")} {name}: {state} '{msg}' in {secs:F1}s, PING left={pingLeft}");
            Services.TaskRunner.KillByNames(new[] { "PING.EXE" });
        }

        // 1. 啟動器開完子程式就自己結束 → 必須等子程式（約 6 秒）
        await Case("launcher-exits-early", "/c start \"\" /b powershell -NoProfile -Command Start-Sleep 6", "", false, 5,
            (s, t) => t >= 5.5);
        // 2. 工具開了「遊戲」(ping) 之後自己跑 3 秒結束 → 不等遊戲；完成後關閉遊戲
        await Case("ignore-game-and-kill", "/c start \"\" /min ping -n 120 127.0.0.1 & powershell -NoProfile -Command Start-Sleep 3", "PING.EXE", true, 5,
            (s, t) => t < 20 && System.Diagnostics.Process.GetProcessesByName("PING").Length == 0, gameStartMin: 1);
        // 3. 使用者中止 → 關閉工具，狀態為 Stopped
        await Case("user-stop", "/c powershell -NoProfile -Command Start-Sleep 60", "", false, 5,
            (s, t) => s == RunState.Stopped && t < 15, cancelAfterMs: 3000);
        // 3b. 工具開著但遊戲一直沒出現（MaaEnd 沒設定自動執行）→ 1 分鐘後判定失敗並關閉工具
        await Case("game-never-starts", "/c powershell -NoProfile -Command Start-Sleep 300", "NeverStartedGame.exe", false, 5,
            (s, t) => s == RunState.Failed && t >= 59 && t < 75, gameStartMin: 1);
        // 4. 結束代碼非 0 → Warning
        await Case("exit-code", "/c exit 3", "", false, 5, (s, _) => s == RunState.Warning);
        // 5. 找不到程式 → Failed
        var missing = new GameTask { Name = "missing", ExePath = @"C:\nope\nothing.exe" };
        var (ms, mm, _) = await Services.TaskRunner.RunOnceAsync(missing, settings, CancellationToken.None);
        sb.AppendLine($"{(ms == RunState.Failed ? "PASS" : "FAIL")} missing-exe: {ms} '{mm}'");

        CursorKeeperSelfTest(sb);
        await MuMuSelfTest(sb);
        await CloseCopiesSelfTest(sb, settings, cmd);
        await LogCheckSelfTest(sb, settings, cmd);
        ReplayRealLogs(sb);
        return sb.ToString();
    }

    /// <summary>
    /// 用真的 MuMuManager 查詢模擬器狀態。只做查詢與「已經開著」的判斷，
    /// 不會啟動或關閉使用者的模擬器。
    /// </summary>
    private static async Task MuMuSelfTest(System.Text.StringBuilder sb)
    {
        // 解讀 JSON（不需要 MuMu）
        var single = Services.MuMu.Parse("{\"index\":\"0\",\"name\":\"Android Device\",\"is_process_started\":true,\"is_android_started\":true,\"player_state\":\"start_finished\"}", 0);
        var all = Services.MuMu.Parse("{\"0\":{\"index\":\"0\",\"name\":\"A\",\"is_android_started\":false,\"is_process_started\":true,\"player_state\":\"starting_rom\"},\"2\":{\"index\":\"2\",\"name\":\"B\"}}", 0);
        var error = Services.MuMu.Parse("{\n  \"errcode\": -200,\n  \"errmsg\": \"player index not found\"\n}", 56);
        var garbage = Services.MuMu.Parse("", 1);
        var parseOk = single.Players is [{ Index: 0, Ready: true }]
                      && all.Players is [{ Index: 0, Ready: false, ProcessStarted: true }, { Index: 2, Ready: false, ProcessStarted: false }]
                      && error.Players.Count == 0 && error.Error == "player index not found"
                      && garbage.Error != null;
        sb.AppendLine($"{(parseOk ? "PASS" : "FAIL")} mumu-parse: single={single.Players.Count} all={all.Players.Count} error='{error.Error}' empty='{garbage.Error}'");

        var manager = Services.MuMu.FindManager();
        if (manager == null)
        {
            sb.AppendLine("SKIP mumu-real: 這台電腦找不到 MuMuManager.exe");
            return;
        }

        var (players, queryError) = await Services.MuMu.QueryAllAsync(manager);
        var resolvedFromDir = Services.MuMu.ResolveManager(Path.GetDirectoryName(Path.GetDirectoryName(manager)));
        var resolvedFromOtherExe = Services.MuMu.ResolveManager(Path.Combine(Path.GetDirectoryName(manager)!, "adb.exe"));
        sb.AppendLine($"{(players.Count > 0 && resolvedFromDir == manager && resolvedFromOtherExe == manager ? "PASS" : "FAIL")} mumu-query: {manager} → " +
                      string.Join("、", players.Select(p => $"#{p.Index} {p.Name} {p.StateText}")) + (queryError != null ? $" 錯誤={queryError}" : ""));

        // 不存在的編號 → 直接失敗、不重試
        var missing = new GameTask { Name = "mumu-missing", StartEmulator = true, EmulatorPath = manager, EmulatorIndex = 99 };
        var missingResult = await Services.MuMu.EnsureRunningAsync(missing, null, CancellationToken.None);
        sb.AppendLine($"{(missingResult is { State: RunState.Failed, Retryable: false } ? "PASS" : "FAIL")} mumu-missing-index: {missingResult?.State} '{missingResult?.Message}'");

        // 已經開著的模擬器 → 不重開、馬上通過
        if (players.FirstOrDefault(p => p.Ready) is { } running)
        {
            var task = new GameTask { Name = "mumu-running", StartEmulator = true, EmulatorPath = "", EmulatorIndex = running.Index };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Services.MuMu.EnsureRunningAsync(task, null, CancellationToken.None);
            sb.AppendLine($"{(result == null && sw.Elapsed < TimeSpan.FromSeconds(5) ? "PASS" : "FAIL")} mumu-already-running: #{running.Index} {(result == null ? "可以直接使用" : result.Message)} in {sw.Elapsed.TotalSeconds:F1}s");
        }
        else
        {
            sb.AppendLine("SKIP mumu-already-running: 目前沒有開著的模擬器（測試不會自己開模擬器）");
        }
    }

    /// <summary>工具已經開著時，要先關掉舊的再啟動新的；用複製出來的 cmd.exe 當作「工具」。</summary>
    private static async Task CloseCopiesSelfTest(System.Text.StringBuilder sb, AppSettings settings, string cmd)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "autodaily-copytest")).FullName;
        var fake = Path.Combine(dir, "fake-tool.exe");
        File.Copy(cmd, fake, overwrite: true);

        var old = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fake, "/c ping -n 120 127.0.0.1 >nul")
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        await Task.Delay(1000);

        var logged = new List<string>();
        void OnLog(Services.LogEntry e)
        {
            lock (logged) logged.Add(e.Message);
        }
        Services.Logger.Logged += OnLog;
        var task = new GameTask
        {
            Name = "close-copies", ExePath = fake, Arguments = "/c ping -n 3 127.0.0.1 >nul",
            KillGameAfter = false, TimeoutMinutes = 2, GameStartTimeoutMinutes = 0,
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (state, msg, _) = await Services.TaskRunner.RunOnceAsync(task, settings, CancellationToken.None);
        Services.Logger.Logged -= OnLog;

        var oldGone = old.WaitForExit(2000);
        var noted = logged.Any(m => m.Contains("已經開著"));
        sb.AppendLine($"{(oldGone && noted ? "PASS" : "FAIL")} close-running-copy: 舊的已關閉={oldGone} 有記錄={noted} 新的={state} '{msg}' in {sw.Elapsed.TotalSeconds:F1}s");
        if (!oldGone)
        {
            try
            {
                old.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
        old.Dispose();
        Services.TaskRunner.KillByNames(new[] { "PING.EXE" });
    }

    /// <summary>把目前的前景視窗當成「遊戲」，檢查視窗範圍與游標判斷；只檢查不移動游標，以免干擾使用者。</summary>
    private static void CursorKeeperSelfTest(System.Text.StringBuilder sb)
    {
        var name = Services.CursorKeeper.ForegroundProcessName();
        if (name == null)
        {
            sb.AppendLine("SKIP cursor-keeper: 目前沒有前景視窗");
            return;
        }

        var check = new Services.CursorKeeper(new[] { name + ".exe" }).Inspect();
        var notGame = new Services.CursorKeeper(new[] { "SurelyNotARunningGame.exe" }).Inspect();
        var ok = check is { } c && c.Right > c.Left && c.Bottom > c.Top
                 && c.Outside == !(c.CursorX >= c.Left && c.CursorX < c.Right && c.CursorY >= c.Top && c.CursorY < c.Bottom)
                 && notGame == null;
        sb.AppendLine($"{(ok ? "PASS" : "FAIL")} cursor-keeper: 前景={name}.exe 畫面=({check?.Left},{check?.Top})-({check?.Right},{check?.Bottom}) " +
                      $"游標=({check?.CursorX},{check?.CursorY}) 在外面={check?.Outside} 非遊戲時不動作={notGame == null}");
    }

    /// <summary>用 PowerShell 腳本模擬工具寫記錄檔，驗證完成檢查、重試、輪替與卡住偵測。</summary>
    private static async Task LogCheckSelfTest(System.Text.StringBuilder sb, AppSettings settings, string cmd)
    {
        var root = Path.Combine(Path.GetTempPath(), "autodaily-logtest");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var logDir = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
        var bgi = Services.Presets.All.First(p => p.Key == "genshin");

        async Task Case(string name, string script, Func<RunState, string, double, bool> expect, Action? before = null,
            string? success = null, string? failure = null, string logPattern = "*.log", int retries = 0, int stall = 0)
        {
            foreach (var f in Directory.GetFiles(logDir)) File.Delete(f);
            before?.Invoke();
            var ps1 = Path.Combine(root, name + ".ps1");
            File.WriteAllText(ps1, "$log = '" + logDir + "'\r\n" + script, new System.Text.UTF8Encoding(true));
            var task = new GameTask
            {
                Name = name, ExePath = cmd,
                Arguments = $"/c powershell -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"",
                KillGameAfter = false, TimeoutMinutes = 5, GameStartTimeoutMinutes = 0,
                LogPaths = Path.Combine(logDir, logPattern),
                SuccessMarkers = success ?? bgi.SuccessMarkers,
                FailureMarkers = failure ?? bgi.FailureMarkers,
                MaxRetries = retries, LogStallMinutes = stall,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (state, msg, _) = await Services.TaskRunner.RunWithRetriesAsync(task, settings, CancellationToken.None);
            var secs = sw.Elapsed.TotalSeconds;
            lock (sb) sb.AppendLine($"{(expect(state, msg, secs) ? "PASS" : "FAIL")} {name}: {state} '{msg}' in {secs:F1}s");
        }

        const string W = "function W($f, $s) { Add-Content -Path (Join-Path $log $f) -Value $s -Encoding UTF8 }\r\n";

        // 6. 開始前就有的舊失敗訊息不算；這次寫入完成訊息 → 成功（且不會因為跑太快被判異常）
        await Case("log-success-ignores-old",
            W + "W 'bgi.log' '工作中'; Start-Sleep 2; W 'bgi.log' '一条龙和配置组任务结束'",
            (s, m, _) => s == RunState.Success && m.Contains("記錄檔確認"),
            before: () => File.WriteAllText(Path.Combine(logDir, "bgi.log"), "昨天的 前往冒险家协会领取奖励执行异常\r\n", System.Text.Encoding.UTF8));

        // 7. 樹脂用完的「无法领取奖励」與單獨一行的「自动地脉花执行失败」不算失敗
        await Case("log-resin-out-is-ok",
            W + "W 'bgi.log' '自动地脉花执行失败'; W 'bgi.log' '自动地脉花执行失败:无法领取奖励'; W 'bgi.log' '自动地脉花执行失败: 无法领取奖励'; W 'bgi.log' '一条龙和配置组任务结束'",
            (s, _, _) => s == RunState.Success);

        // 8. 第一次出現失敗訊息 → 自動重跑一次 → 第二次成功
        await Case("log-retry-then-success",
            W + "$c = Join-Path $log 'count.txt'; $n = 0; if (Test-Path $c) { $n = [int](Get-Content $c) }; Set-Content $c ($n + 1)\r\n" +
                "if ($n -eq 0) { W 'bgi.log' '任务结束：\"取消自动任务\"'; W 'bgi.log' '一条龙和配置组任务结束' } else { W 'bgi.log' '一条龙和配置组任务结束' }",
            (s, m, _) => s == RunState.Success && m.Contains("重試 1 次後成功"),
            retries: 1);

        // 9. 程式結束但沒有完成訊息 → 未完成
        await Case("log-no-success-marker",
            W + "W 'bgi.log' '做到一半就結束了'",
            (s, _, _) => s == RunState.Incomplete);

        // 10. 記錄檔輪替：失敗訊息寫進舊檔後立刻改名，再開新檔寫完成 → 仍要抓到失敗
        await Case("log-rotation-keeps-failure",
            W + "W 'maafw.log' 'msg=Tasker.Task.Failed entry=VisitFriendsMain'; Rename-Item (Join-Path $log 'maafw.log') 'maafw.bak.1.log'; W 'maafw.log' '新檔'; W 'mxu.log' '自动执行任务完成，关闭自身'",
            (s, m, _) => s == RunState.Incomplete && m.Contains("Tasker.Task.Failed"),
            before: () => File.WriteAllText(Path.Combine(logDir, "maafw.log"), "舊內容 Tasker.Task.Failed（開始前的，不算）\r\n"),
            success: "自动执行任务完成", failure: "Tasker.Task.Failed");

        // 11. 記錄檔設錯 → 不重跑，只提醒無法確認
        await Case("log-path-wrong",
            W + "Start-Sleep 1",
            (s, m, _) => s == RunState.Warning && m.Contains("找不到記錄檔"),
            logPattern: @"nope\*.log", retries: 1);

        // 12. 記錄檔 1 分鐘沒動 → 判定卡住並關閉
        await Case("log-stall",
            W + "W 'bgi.log' '開始'; Start-Sleep 150",
            (s, m, t) => s == RunState.Incomplete && m.Contains("卡住") && t >= 59 && t < 80,
            stall: 1);
    }

    /// <summary>
    /// 把開發者電腦上各工具的真實記錄檔餵給 LogMonitor，確認預設的完成／失敗訊息判斷正確。
    /// 工具都裝在 AUTODAILY_TOOLS 環境變數指定的資料夾（預設 D:\Auto）；找不到的工具會略過。
    /// 「預期失敗」的日期與檔案是人工比對過記錄檔內容後列出的。
    /// </summary>
    private static void ReplayRealLogs(System.Text.StringBuilder sb)
    {
        var tools = Environment.GetEnvironmentVariable("AUTODAILY_TOOLS") is { Length: > 0 } custom ? custom : @"D:\Auto";
        var sandbox = Path.Combine(Path.GetTempPath(), "autodaily-replay");

        (string? Success, string? Failure) Replay(string file, Services.PresetInfo p)
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.CreateDirectory(sandbox);
            var monitor = Services.LogMonitor.Create(Path.Combine(sandbox, "*"), p.SuccessMarkers, p.FailureMarkers, sandbox);
            monitor.Begin();
            File.Copy(file, Path.Combine(sandbox, "replay.log"));
            monitor.Finish();
            return (monitor.SuccessLine, monitor.FailureLine);
        }

        var bgiDir = Path.Combine(tools, @"BetterGI\log");
        if (Directory.Exists(bgiDir))
        {
            // 依先前人工分析：這些日子的一條龍有真的沒完成的項目
            var badDays = new HashSet<string> { "20261031", "20261102", "20261103", "20261104", "20261106", "20261107",
                "20261108", "20261109", "20261110", "20261111", "20261114", "20261117" };
            var preset = Services.Presets.All.First(p => p.Key == "genshin");
            var wrong = new List<string>();
            var files = Directory.GetFiles(bgiDir, "better-genshin-impact2026*.log")
                .Where(f => string.CompareOrdinal(Path.GetFileName(f), "better-genshin-impact20261028.log") >= 0)
                // 只看有跑一條龍的日子（手動開 BetterGI 沒跑一條龍的那天本來就不會有完成訊息）
                .Where(f => File.ReadAllText(f).Contains("启用一条龙配置"))
                .OrderBy(f => f).ToList();
            foreach (var f in files)
            {
                var day = Path.GetFileNameWithoutExtension(f)[^8..];
                var (ok, bad) = Replay(f, preset);
                if ((bad != null) != badDays.Contains(day) || ok == null) wrong.Add($"{day}(fail={bad}, ok={ok != null})");
            }
            sb.AppendLine($"{(wrong.Count == 0 ? "PASS" : "FAIL")} replay-bettergi: {files.Count} 天 {string.Join("; ", wrong)}");
        }

        var zzzDir = Path.Combine(tools, @"ZZZ\.log");
        if (Directory.Exists(zzzDir))
        {
            var preset = Services.Presets.All.First(p => p.Key == "zzz");
            var results = Directory.GetFiles(zzzDir, "log.txt*").Select(f => (f, r: Replay(f, preset))).ToList();
            var allGood = results.All(x => x.r.Success != null && x.r.Failure == null);
            sb.AppendLine($"{(allGood ? "PASS" : "FAIL")} replay-zzz: {results.Count} 個檔 " +
                          string.Join("; ", results.Where(x => x.r.Success == null || x.r.Failure != null).Select(x => $"{Path.GetFileName(x.f)} fail={x.r.Failure}")));
        }

        var maaDir = Path.Combine(tools, @"MaaEnd-win-x86_64-v2.2.0\debug");
        if (Directory.Exists(maaDir))
        {
            var preset = Services.Presets.All.First(p => p.Key == "endfield");
            // MaaEnd 只保留約 21 個輪替檔，舊的會被刪掉；這裡列出人工確認過有任務失敗的檔案
            var expectedFail = new[]
            {
                "maafw.bak.2026.11.10-16.24.42.585.log", "maafw.bak.2026.11.11-16.26.28.473.log",
                "maafw.bak.2026.11.11-16.32.00.213.log", "maafw.bak.2026.11.18-16.33.03.461.log",
            };
            var wrong = new List<string>();
            var files = Directory.GetFiles(maaDir, "maafw.bak.2026.11.1*.log");
            foreach (var f in files)
            {
                var (_, bad) = Replay(f, preset);
                if ((bad != null) != expectedFail.Contains(Path.GetFileName(f))) wrong.Add(Path.GetFileName(f));
            }
            var mxuOk = Directory.GetFiles(maaDir, "2026-11-1*.log").Count(f => Replay(f, preset).Success != null);
            // 長行要從符合處附近截取，才看得到是哪個任務失敗
            var sample = files.Where(f => expectedFail.Contains(Path.GetFileName(f))).Select(f => Replay(f, preset).Failure).FirstOrDefault(x => x != null);
            sb.AppendLine($"{(sample != null && sample.Contains("Tasker.Task.Failed") && sample.Contains("entry") ? "PASS" : "FAIL")} failure-snippet: {sample}");
            sb.AppendLine($"{(wrong.Count == 0 ? "PASS" : "FAIL")} replay-maaend: {files.Length} 個 maafw 檔 {string.Join("; ", wrong)}；MXU 日誌有完成訊息 {mxuOk} 個");
        }

        var duduDir = Path.Combine(tools, @"MaaDuDuL-win-x86_64-v1.4.0\logs");
        if (Directory.Exists(duduDir))
        {
            var preset = Services.Presets.All.First(p => p.Key == "dudul");
            var segmentFile = Path.Combine(Path.GetTempPath(), "autodaily-dudul-segment.log");

            (string? Success, string? Failure) ReplayLines(IEnumerable<string> lines)
            {
                File.WriteAllLines(segmentFile, lines, new System.Text.UTF8Encoding(false));
                return Replay(segmentFile, preset);
            }

            // 把每天的記錄切成一次一次的執行：從「開始任務：啟動遊戲」到「全部完成／運行失敗／放棄」
            var runStart = new System.Text.RegularExpressions.Regex("(開始任務|开始任务)：🟢");
            var runEnd = new System.Text.RegularExpressions.Regex("任[务務]已全部完成|任[务務](運行|运行)失[敗败]|已放[棄弃]本次任[务務]");
            var taskFailed = new System.Text.RegularExpressions.Regex("任[务務]失[敗败]：");
            int runs = 0, okRuns = 0;
            var wrong = new List<string>();
            foreach (var file in Directory.GetFiles(duduDir, "log-*.log").OrderBy(f => f))
            {
                List<string>? current = null;
                foreach (var line in ReadShared(file))
                {
                    if (runStart.IsMatch(line)) current = new List<string>();
                    if (current == null) continue;
                    current.Add(line);
                    if (!runEnd.IsMatch(line)) continue;

                    runs++;
                    var expectOk = line.Contains("全部完成") && !current.Any(taskFailed.IsMatch);
                    if (expectOk) okRuns++;
                    var (ok, bad) = ReplayLines(current);
                    if ((ok != null && bad == null) != expectOk)
                        wrong.Add($"{line[1..20]}(預期{(expectOk ? "完成" : "未完成")}, ok={ok != null}, fail={bad})");
                    current = null;
                }
            }

            // 連不上模擬器時它也會顯示「任務已全部完成」，必須判定為沒完成（11/18 17:55 實際發生過）
            var today = Path.Combine(duduDir, "log-20261118.log");
            var connectFail = File.Exists(today)
                ? ReplayLines(ReadShared(today).Where(l => string.CompareOrdinal(l, "[2026-11-18 17:55:41") >= 0
                                                               && string.CompareOrdinal(l, "[2026-11-18 17:55:43") < 0))
                : (null, null);
            var connectOk = connectFail.Success != null && connectFail.Failure?.Contains("初始化控制器失败") == true;
            sb.AppendLine($"{(connectOk ? "PASS" : "FAIL")} dudul-connect-fail: ok={connectFail.Success != null} fail={connectFail.Failure}");
            sb.AppendLine($"{(wrong.Count == 0 && runs > 0 ? "PASS" : "FAIL")} replay-dudul: {runs} 次執行（完成 {okRuns}） {string.Join("; ", wrong)}");
        }
    }

    /// <summary>讀取工具正在寫入中的記錄檔（不能用 File.ReadLines，會跟寫入端衝突）。</summary>
    private static List<string> ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
#endif
