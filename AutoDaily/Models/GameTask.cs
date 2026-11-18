using System.Text.Json;
using System.Text.Json.Serialization;
using AutoDaily.Services;

namespace AutoDaily.Models;

/// <summary>一個要依序執行的每日任務（一個遊戲 + 一個自動化工具）。</summary>
public sealed class GameTask : ObservableObject
{
    private string _name = "新任務";
    private string _badge = "★";
    private string _color = "#7C6CFF";
    private string _toolName = "";
    private string _exePath = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private string _gameProcesses = "";
    private int _timeoutMinutes = 120;
    private int _gameStartTimeoutMinutes = 10;
    private bool _killGameAfter = true;
    private bool _trackChildren = true;
    private bool _enabled = true;
    private string _tip = "";
    private string? _logPaths;
    private string? _successMarkers;
    private string? _failureMarkers;
    private int _maxRetries = 1;
    private int _logStallMinutes = 10;
    private bool? _keepCursorInGame;
    private bool _startEmulator;
    private string _emulatorPath = "";
    private int _emulatorIndex;
    private DateTime? _lastRunAt;
    private int? _lastDurationSeconds;
    private RunState? _lastResult;

    private RunState _state = RunState.Idle;
    private TimeSpan _elapsed;
    private int _order;
    private string _message = "";
    private bool _isDragging;
    private int _attempt;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Preset { get; set; } = "custom";

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Badge { get => _badge; set => SetProperty(ref _badge, value); }
    public string Color { get => _color; set => SetProperty(ref _color, value); }

    public string ToolName
    {
        get => _toolName;
        set { if (SetProperty(ref _toolName, value)) OnPropertyChanged(nameof(Subtitle)); }
    }

    public string ExePath
    {
        get => _exePath;
        set
        {
            if (!SetProperty(ref _exePath, value)) return;
            OnPropertyChanged(nameof(HasExe));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string Arguments
    {
        get => _arguments;
        set { if (SetProperty(ref _arguments, value)) OnPropertyChanged(nameof(Subtitle)); }
    }

    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public string GameProcesses { get => _gameProcesses; set => SetProperty(ref _gameProcesses, value); }
    public int TimeoutMinutes { get => _timeoutMinutes; set => SetProperty(ref _timeoutMinutes, value); }

    /// <summary>工具啟動後，遊戲必須在幾分鐘內出現，否則判定工具沒有開始執行（0 = 不檢查）。</summary>
    public int GameStartTimeoutMinutes { get => _gameStartTimeoutMinutes; set => SetProperty(ref _gameStartTimeoutMinutes, value); }
    public bool KillGameAfter { get => _killGameAfter; set => SetProperty(ref _killGameAfter, value); }
    public bool TrackChildren { get => _trackChildren; set => SetProperty(ref _trackChildren, value); }

    /// <summary>執行期間遊戲在前景時，把跑出遊戲視窗的游標移回中央（null = 舊設定檔，載入時依預設帶入）。</summary>
    public bool? KeepCursorInGame { get => _keepCursorInGame; set => SetProperty(ref _keepCursorInGame, value); }
    public string Tip { get => _tip; set => SetProperty(ref _tip, value); }

    // ───── 模擬器（手機遊戲）─────

    /// <summary>執行前先開啟 MuMu 模擬器並等 Android 開機完成，自動化程式一開就連得到。</summary>
    public bool StartEmulator { get => _startEmulator; set => SetProperty(ref _startEmulator, value); }

    /// <summary>MuMuManager.exe 的位置（空白 = 自動尋找）。</summary>
    public string EmulatorPath { get => _emulatorPath; set => SetProperty(ref _emulatorPath, value); }

    /// <summary>MuMu 多開器裡的第幾台（MuMuManager 的 -v 編號）。</summary>
    public int EmulatorIndex { get => _emulatorIndex; set => SetProperty(ref _emulatorIndex, value); }

    // ───── 完成檢查（看記錄檔）─────
    // null 代表舊設定檔沒有這個欄位，載入時會從預設帶入；空字串代表使用者刻意不檢查。

    /// <summary>記錄檔位置，一行一個，相對於工具資料夾，可用萬用字元。</summary>
    public string? LogPaths { get => _logPaths; set => SetProperty(ref _logPaths, value); }

    /// <summary>完成時會出現的訊息（任一行符合即可）。</summary>
    public string? SuccessMarkers { get => _successMarkers; set => SetProperty(ref _successMarkers, value); }

    /// <summary>代表沒完成的訊息（任一行出現就判定未完成）。</summary>
    public string? FailureMarkers { get => _failureMarkers; set => SetProperty(ref _failureMarkers, value); }

    /// <summary>沒完成時自動重跑幾次。</summary>
    public int MaxRetries { get => _maxRetries; set => SetProperty(ref _maxRetries, value); }

    /// <summary>記錄檔超過幾分鐘沒更新就判定卡住（0 = 不檢查）。</summary>
    public int LogStallMinutes { get => _logStallMinutes; set => SetProperty(ref _logStallMinutes, value); }

    public bool Enabled
    {
        get => _enabled;
        set { if (SetProperty(ref _enabled, value)) RaiseStatus(); }
    }

    public DateTime? LastRunAt
    {
        get => _lastRunAt;
        set { if (SetProperty(ref _lastRunAt, value)) RaiseHistory(); }
    }

    public int? LastDurationSeconds
    {
        get => _lastDurationSeconds;
        set { if (SetProperty(ref _lastDurationSeconds, value)) RaiseHistory(); }
    }

    public RunState? LastResult
    {
        get => _lastResult;
        set { if (SetProperty(ref _lastResult, value)) RaiseHistory(); }
    }

    // ───── 以下為執行期狀態，不寫入設定檔 ─────

    [JsonIgnore]
    public RunState State
    {
        get => _state;
        set { if (SetProperty(ref _state, value)) RaiseStatus(); }
    }

    [JsonIgnore]
    public TimeSpan Elapsed
    {
        get => _elapsed;
        set
        {
            // 只在秒數改變時通知，避免每次輪詢都重繪
            var secondChanged = (int)_elapsed.TotalSeconds != (int)value.TotalSeconds;
            _elapsed = value;
            if (!secondChanged && value != TimeSpan.Zero) return;
            OnPropertyChanged();
            if (_state == RunState.Running) OnPropertyChanged(nameof(StatusText));
        }
    }

    [JsonIgnore]
    public int Order
    {
        get => _order;
        set { if (SetProperty(ref _order, value)) OnPropertyChanged(nameof(OrderText)); }
    }

    [JsonIgnore] public string OrderText => Order.ToString("00");

    [JsonIgnore] public string Message { get => _message; set => SetProperty(ref _message, value); }

    [JsonIgnore] public bool IsDragging { get => _isDragging; set => SetProperty(ref _isDragging, value); }

    /// <summary>目前是第幾次重試（0 = 第一次執行）。</summary>
    [JsonIgnore]
    public int Attempt
    {
        get => _attempt;
        set { if (SetProperty(ref _attempt, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    [JsonIgnore]
    public bool HasExe => !string.IsNullOrWhiteSpace(ExePath) && File.Exists(TaskRunner.CleanPath(ExePath));

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            if (!HasExe)
                return string.IsNullOrWhiteSpace(ExePath)
                    ? "尚未設定程式路徑 — 點右側齒輪進行設定"
                    : "找不到程式：" + ExePath;
            var tool = string.IsNullOrWhiteSpace(ToolName) ? Path.GetFileNameWithoutExtension(ExePath) : ToolName;
            return string.IsNullOrWhiteSpace(Arguments) ? tool : $"{tool}  ·  {Arguments}";
        }
    }

    [JsonIgnore]
    public bool DoneToday => LastRunAt?.Date == DateTime.Today && LastResult == RunState.Success;

    [JsonIgnore]
    public string LastRunText
    {
        get
        {
            if (LastRunAt is not DateTime at) return "尚未執行過";
            var day = at.Date == DateTime.Today ? "今天"
                : at.Date == DateTime.Today.AddDays(-1) ? "昨天"
                : at.ToString("M/d");
            var result = LastResult switch
            {
                RunState.Success => "完成",
                RunState.Warning => "異常結束",
                RunState.Incomplete => "未完成",
                RunState.Failed => "失敗",
                RunState.Timeout => "逾時",
                RunState.Stopped => "已中止",
                _ => "",
            };
            var duration = LastDurationSeconds is int s ? $" · 耗時 {FormatDuration(TimeSpan.FromSeconds(s))}" : "";
            return $"上次 {day} {at:HH:mm} · {result}{duration}";
        }
    }

    /// <summary>狀態標籤的樣式種類（給 XAML DataTrigger 使用）。</summary>
    [JsonIgnore]
    public string ChipKind => State switch
    {
        RunState.Running => "running",
        RunState.Queued => "queued",
        RunState.Success => "success",
        RunState.Warning or RunState.Incomplete => "warn",
        RunState.Failed or RunState.Timeout => "fail",
        RunState.Stopped => "stopped",
        _ => !Enabled ? "off" : DoneToday ? "done" : "idle",
    };

    [JsonIgnore]
    public string StatusText => State switch
    {
        RunState.Running => (Attempt > 0 ? $"重試 {Attempt} · " : "執行中 ")
                            + (Elapsed.TotalHours >= 1 ? Elapsed.ToString(@"h\:mm\:ss") : Elapsed.ToString(@"mm\:ss")),
        RunState.Queued => "排隊中",
        RunState.Success => "完成",
        RunState.Warning => "異常結束",
        RunState.Incomplete => "未完成",
        RunState.Failed => "失敗",
        RunState.Timeout => "逾時",
        RunState.Stopped => "已中止",
        _ => !Enabled ? "不執行" : DoneToday ? "今日已完成" : "待命",
    };

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ChipKind));
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(LastRunText));
        OnPropertyChanged(nameof(DoneToday));
        RaiseStatus();
    }

    /// <summary>跨日時重新整理「今天/昨天」等文字。</summary>
    public void RefreshTimeTexts()
    {
        RaiseHistory();
        OnPropertyChanged(nameof(HasExe));
        OnPropertyChanged(nameof(Subtitle));
    }

    public GameTask Clone()
    {
        var copy = JsonSerializer.Deserialize<GameTask>(JsonSerializer.Serialize(this, ConfigService.JsonOptions), ConfigService.JsonOptions)!;
        return copy;
    }

    /// <summary>從編輯後的複本取回可設定的欄位。</summary>
    public void CopyFrom(GameTask o)
    {
        Preset = o.Preset;
        Name = o.Name;
        Badge = o.Badge;
        Color = o.Color;
        ToolName = o.ToolName;
        ExePath = o.ExePath;
        Arguments = o.Arguments;
        WorkingDirectory = o.WorkingDirectory;
        GameProcesses = o.GameProcesses;
        TimeoutMinutes = o.TimeoutMinutes;
        GameStartTimeoutMinutes = o.GameStartTimeoutMinutes;
        KillGameAfter = o.KillGameAfter;
        TrackChildren = o.TrackChildren;
        KeepCursorInGame = o.KeepCursorInGame;
        StartEmulator = o.StartEmulator;
        EmulatorPath = o.EmulatorPath;
        EmulatorIndex = o.EmulatorIndex;
        Enabled = o.Enabled;
        Tip = o.Tip;
        LogPaths = o.LogPaths;
        SuccessMarkers = o.SuccessMarkers;
        FailureMarkers = o.FailureMarkers;
        MaxRetries = o.MaxRetries;
        LogStallMinutes = o.LogStallMinutes;
    }

    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} 秒";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分 {d.Seconds} 秒";
        return $"{(int)d.TotalHours} 小時 {d.Minutes} 分";
    }
}
