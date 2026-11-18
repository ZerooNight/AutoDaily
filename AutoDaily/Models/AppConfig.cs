using System.Text.Json;
using AutoDaily.Services;

namespace AutoDaily.Models;

public enum RunState
{
    Idle,
    Queued,
    Running,
    Success,
    Warning,
    /// <summary>程式結束了，但記錄檔顯示任務沒有真的完成。</summary>
    Incomplete,
    Failed,
    Timeout,
    Stopped,
}

public enum PostAction
{
    None,
    CloseApp,
    Sleep,
    Shutdown,
}

public sealed class AppSettings
{
    public PostAction PostAction { get; set; } = PostAction.None;

    /// <summary>每個任務之間的等待秒數。</summary>
    public int DelayBetweenSeconds { get; set; } = 10;

    /// <summary>不需要等待它結束的背景程式（例如常駐的 adb）。</summary>
    public string IgnoreProcesses { get; set; } =
        "conhost.exe; adb.exe; WerFault.exe; UnityCrashHandler64.exe; CrashReportClient.exe";

    public bool ScheduleEnabled { get; set; }
    public string ScheduleTime { get; set; } = "05:00";
    public bool ScheduleCatchUp { get; set; }
    public bool ScheduleWake { get; set; }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, ConfigService.JsonOptions), ConfigService.JsonOptions)!;
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<GameTask> Tasks { get; set; } = new();
}
