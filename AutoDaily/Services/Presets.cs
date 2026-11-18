using AutoDaily.Models;

namespace AutoDaily.Services;

public sealed record PresetInfo(
    string Key,
    string Name,
    string ShortName,
    string Badge,
    string Color,
    string ToolName,
    string Arguments,
    string GameProcesses,
    string Tip,
    string[] ExeKeywords,
    string LogPaths,
    string SuccessMarkers,
    string FailureMarkers,
    bool KeepCursorInGame = false,
    bool UsesEmulator = false);

/// <summary>各款遊戲自動化工具的預設啟動方式與完成判斷。</summary>
public static class Presets
{
    // 完成／未完成訊息取自各工具的實際記錄檔與原始碼
    public static readonly PresetInfo[] All =
    {
        new("genshin", "原神", "原神 · BetterGI", "原", "#5AB4F5", "BetterGI",
            "startOneDragon",
            "GenshinImpact.exe; YuanShen.exe",
            "選擇 BetterGI 資料夾中的 BetterGI.exe。\n" +
            "請在 BetterGI「一條龍」頁面把「完成後操作」設為「關閉遊戲和軟件」，跑完才會自動接續下一個遊戲。\n" +
            "想指定一條龍配置時，參數可寫成：startOneDragon 配置名稱",
            new[] { "bettergi", "better-gi", "bettergenshin" },
            @"log\better-genshin-impact*.log",
            "一条龙和配置组任务结束",
            "取消自动任务\n" +
            "# 「无法领取奖励」是树脂用完的正常结束，不算失败\n" +
            @"re:执行失败\s*[:：](?!\s*无法领取奖励)" + "\n" +
            "执行异常\n" +
            "奖励异常",
            KeepCursorInGame: true),

        new("zzz", "絕區零", "絕區零 · 一條龍", "絕", "#F5C842", "ZZZ 一條龍",
            "-o -c",
            "ZenlessZoneZero.exe",
            "選擇絕區零一條龍安裝資料夾中的啟動器（OneDragon 開頭的 .exe）。\n" +
            "參數 -o 代表執行一條龍、-c 代表完成後關閉遊戲；多帳號可再加上 -i 1,2 指定實例。\n" +
            "一條龍會記住今天已完成的項目，重試時只會補跑沒完成的部分。",
            new[] { "onedragon", "zenless", "zzz" },
            @".log\log.txt",
            "指令[ 一条龙 ] 执行成功",
            "# 「等待大世界画面」每次都會失敗幾次再重試，是正常的\n" +
            @"re:\[ERROR\]: 指令\[ (?!等待大世界画面 \])[^\]]+ \] 执行失败"),

        new("endfield", "明日方舟：終末地", "終末地 · MaaEnd", "終", "#3DDBA4", "MaaEnd",
            "--autostart -q",
            "Endfield.exe",
            "選擇 MaaEnd 資料夾中的 MaaEnd.exe。\n" +
            "必須先在 MaaEnd「設定」→「啟動後自動執行」選擇要跑的配置（預設是「不自動執行」），否則 MaaEnd 開啟後不會跑任務。\n" +
            "也可以在參數加上 -i \"配置名稱\"（要和 MaaEnd 上方配置分頁的名稱完全相同）。-q 代表跑完後自動關閉 MaaEnd。",
            new[] { "maaend", "mxu" },
            @"debug\*.log",
            "自动执行任务完成",
            "Tasker.Task.Failed"),

        new("wuwa", "鳴潮", "鳴潮 · ok-ww", "鳴", "#B18CFF", "ok-ww",
            "-t 1 -e",
            "Client-Win64-Shipping.exe; Wuthering Waves.exe",
            "選擇 ok-ww 安裝資料夾中的 ok-ww.exe。\n" +
            "-t 1 代表執行 ok-ww 任務清單中的第 1 個任務（請確認是「一條龍／日常」），-e 代表完成後關閉遊戲與 ok-ww。",
            new[] { "ok-ww", "ok_ww", "okww", "wuthering" },
            @"data\apps\ok-ww\working\logs\ok-script*.log",
            "Successfully Executed Task",
            "# ok-ww 出錯後不會自己關閉，會等記錄檔安靜幾分鐘後由本程式關掉重跑\n" +
            "exception stopped"),

        // 手機遊戲：MaaDuDuL 透過 ADB 操作 MuMu 模擬器，模擬器要先開好它才連得上
        new("dudul", "嘟嘟臉惡作劇", "嘟嘟臉 · MaaDuDuL", "嘟", "#FF7A90", "MaaDuDuL",
            "--autostart -q",
            "MuMuNxDevice.exe; MuMuPlayer.exe",
            "選擇 MaaDuDuL 資料夾中的 MaaDuDuL.exe。\n" +
            "下方「模擬器」開啟時，會先開好 MuMu、等 Android 開機完成，再啟動 MaaDuDuL，它就會自動連上模擬器。\n" +
            "--autostart 代表開啟後自動執行、-q 代表跑完後關閉 MaaDuDuL；有多個配置時可加上 -i \"配置名稱\"。" +
            "要跑的項目請先在 MaaDuDuL 裡勾好（第一項要是「啟動遊戲」）。",
            new[] { "maadudul", "dudul" },
            @"logs\log-*.log",
            @"re:任[务務]已全部完成",
            "# 有任何一項失敗，MaaDuDuL 會先顯示「任務失敗：項目名稱」，最後顯示「任務運行失敗」\n" +
            @"re:任[务務]失[败敗][:：]" + "\n" +
            @"re:任[务務](运行|運行)失[败敗]" + "\n" +
            "# 連不上模擬器、截圖逾時或資源載入失敗時，它照樣會顯示「任務已全部完成」\n" +
            "初始化控制器失败\n" +
            @"re:截[图圖](超时|逾時)" + "\n" +
            @"re:(资源加载|資源載入)失[败敗]",
            UsesEmulator: true),
    };

    public static GameTask CreateTask(PresetInfo p)
    {
        var t = new GameTask();
        Apply(t, p);
        return t;
    }

    public static void Apply(GameTask t, PresetInfo p)
    {
        t.Preset = p.Key;
        t.Name = p.Name;
        t.Badge = p.Badge;
        t.Color = p.Color;
        t.ToolName = p.ToolName;
        t.Arguments = p.Arguments;
        t.GameProcesses = p.GameProcesses;
        t.Tip = p.Tip;
        t.LogPaths = p.LogPaths;
        t.SuccessMarkers = p.SuccessMarkers;
        t.FailureMarkers = p.FailureMarkers;
        t.KeepCursorInGame = p.KeepCursorInGame;
        t.StartEmulator = p.UsesEmulator;
        if (p.UsesEmulator && string.IsNullOrWhiteSpace(t.EmulatorPath))
            t.EmulatorPath = MuMu.FindManager() ?? "";
    }

    /// <summary>舊設定檔沒有完成檢查欄位時，從預設帶入（使用者清空過的不動）。</summary>
    public static void FillMissing(GameTask t)
    {
        var p = All.FirstOrDefault(x => x.Key == t.Preset);
        t.LogPaths ??= p?.LogPaths ?? "";
        t.SuccessMarkers ??= p?.SuccessMarkers ?? "";
        t.FailureMarkers ??= p?.FailureMarkers ?? "";
        t.KeepCursorInGame ??= p?.KeepCursorInGame ?? false;
    }

    /// <summary>依照選到的程式檔名猜是哪一個工具。</summary>
    public static PresetInfo? Guess(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        var dir = (Path.GetDirectoryName(exePath) ?? "").ToLowerInvariant();
        return All.FirstOrDefault(p => p.ExeKeywords.Any(k => name.Contains(k)))
               ?? All.FirstOrDefault(p => p.ExeKeywords.Any(k => dir.Contains(k)));
    }
}
