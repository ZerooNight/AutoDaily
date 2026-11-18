using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AutoDaily.Models;
using AutoDaily.Services;

namespace AutoDaily.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly CultureInfo ZhTw = new("zh-TW");

    private readonly AppConfig _config;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _countdownCts;
    private TaskCompletionSource? _idle;

    private bool _isRunning;
    private string _summaryText = "";
    private string _currentText = "準備就緒";
    private string _progressText = "";
    private double _progress;
    private bool _countdownVisible;
    private string _countdownTitle = "";
    private int _countdownSeconds;

    public MainViewModel()
    {
        Logger.Logged += e => _ui.BeginInvoke(() =>
        {
            Logs.Add(e);
            while (Logs.Count > 600) Logs.RemoveAt(0);
        });

        _config = ConfigService.Load();
        foreach (var t in _config.Tasks)
        {
            t.PropertyChanged += OnTaskChanged;
            Tasks.Add(t);
        }
        Tasks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoTasks));
            UpdateSummary();
        };
        Renumber();

        StartCommand = new RelayCommand(_ => _ = RunAsync(), _ => IsIdle);
        StopCommand = new RelayCommand(_ => Stop(), _ => IsRunning);
        ClearLogCommand = new RelayCommand(_ => Logs.Clear());
        CancelCountdownCommand = new RelayCommand(_ => _countdownCts?.Cancel());

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(DateText));
            if (IsIdle)
                foreach (var t in Tasks) t.RefreshTimeTexts();
            UpdateSummary();
        };
        _clock.Start();

        UpdateSummary();
        ProgressText = $"0 / {Tasks.Count(t => t.Enabled)}";
        Logger.Write("準備就緒。拖曳卡片可調整順序，右側開關決定是否執行。");
        foreach (var t in Tasks.Where(t => t.Enabled && !t.HasExe))
            Logger.Write($"{t.Name}：尚未設定程式路徑，請點卡片右側的齒輪設定。", LogLevel.Warn);
    }

    public ObservableCollection<GameTask> Tasks { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public AppSettings Settings => _config.Settings;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CancelCountdownCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !IsRunning;
    public bool HasNoTasks => Tasks.Count == 0;
    public string DateText => DateTime.Now.ToString("M月d日　dddd", ZhTw);

    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }
    public string CurrentText { get => _currentText; private set => SetProperty(ref _currentText, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool CountdownVisible { get => _countdownVisible; private set => SetProperty(ref _countdownVisible, value); }
    public string CountdownTitle { get => _countdownTitle; private set => SetProperty(ref _countdownTitle, value); }
    public int CountdownSeconds { get => _countdownSeconds; private set => SetProperty(ref _countdownSeconds, value); }

    // ───────────────────────── 任務清單 ─────────────────────────

    public void AddTask(GameTask task)
    {
        task.PropertyChanged += OnTaskChanged;
        Tasks.Add(task);
        Renumber();
        Save();
    }

    public void RemoveTask(GameTask task)
    {
        task.PropertyChanged -= OnTaskChanged;
        Tasks.Remove(task);
        Renumber();
        Save();
    }

    public void Renumber()
    {
        for (var i = 0; i < Tasks.Count; i++) Tasks[i].Order = i + 1;
    }

    public void Save()
    {
        try
        {
            _config.Tasks = Tasks.ToList();
            ConfigService.Save(_config);
        }
        catch (Exception ex)
        {
            Logger.Write($"儲存設定失敗：{ex.Message}", LogLevel.Error);
        }
    }

    public void UpdateSummary()
    {
        var enabled = Tasks.Count(t => t.Enabled);
        var done = Tasks.Count(t => t.DoneToday);
        SummaryText = Tasks.Count == 0
            ? "還沒有任何任務，點「新增任務」開始設定"
            : $"已啟用 {enabled} / {Tasks.Count} 項　·　今日已完成 {done} 項";
        if (IsIdle && Progress == 0) ProgressText = $"0 / {enabled}";
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameTask.Enabled)) return;
        UpdateSummary();
        Save();
    }

    // ───────────────────────── 執行 ─────────────────────────

    public async Task RunAsync(GameTask? single = null)
    {
        if (IsRunning) return;
        var queue = single != null ? new List<GameTask> { single } : Tasks.Where(t => t.Enabled).ToList();
        if (queue.Count == 0)
        {
            Logger.Write("沒有任何已啟用的任務。", LogLevel.Warn);
            return;
        }

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        _idle = new TaskCompletionSource();
        var token = _runCts.Token;
        var total = Stopwatch.StartNew();
        var stopped = false;

        Power.KeepAwake(true);
        foreach (var t in Tasks)
        {
            t.State = queue.Contains(t) ? RunState.Queued : RunState.Idle;
            t.Message = "";
        }
        Progress = 0;
        ProgressText = $"0 / {queue.Count}";
        Logger.Write(single != null ? $"══ 單獨執行：{single.Name} ══" : $"══ 開始執行每日任務，共 {queue.Count} 項 ══");

        try
        {
            for (var i = 0; i < queue.Count; i++)
            {
                var t = queue[i];
                CurrentText = $"正在執行　{t.Name}（{i + 1} / {queue.Count}）";
                t.Elapsed = TimeSpan.Zero;
                t.State = RunState.Running;
                var startedAt = DateTime.Now;
                Logger.Write($"▶ {t.Name}");

                var (state, message, _) = await TaskRunner.RunWithRetriesAsync(t, Settings, token);
                t.Attempt = 0;

                t.State = state;
                t.Message = message;
                t.LastRunAt = startedAt;
                t.LastDurationSeconds = (int)t.Elapsed.TotalSeconds;
                t.LastResult = state;
                Logger.Write($"{t.Name}：{message}（{GameTask.FormatDuration(t.Elapsed)}）", LevelOf(state));

                Progress = (i + 1) / (double)queue.Count;
                ProgressText = $"{i + 1} / {queue.Count}";
                Save();
                UpdateSummary();

                if (state == RunState.Stopped || token.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                if (i < queue.Count - 1 && Settings.DelayBetweenSeconds > 0)
                {
                    CurrentText = $"{Settings.DelayBetweenSeconds} 秒後執行下一項：{queue[i + 1].Name}";
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Settings.DelayBetweenSeconds), token);
                    }
                    catch (OperationCanceledException)
                    {
                        stopped = true;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"發生未預期的錯誤：{ex.Message}", LogLevel.Error);
            stopped = true;
        }
        finally
        {
            foreach (var t in queue)
            {
                if (t.State == RunState.Queued) t.State = RunState.Idle;
                if (t.State == RunState.Running) t.State = RunState.Failed;
            }
            Power.KeepAwake(false);
            _runCts.Dispose();
            _runCts = null;
            IsRunning = false;
            _idle.TrySetResult();
        }

        if (stopped)
        {
            CurrentText = "已中止";
            Logger.Write("已中止執行。", LogLevel.Warn);
            return;
        }

        CurrentText = $"全部完成！總耗時 {GameTask.FormatDuration(total.Elapsed)}";
        Logger.Write($"══ {CurrentText} ══", LogLevel.Success);
        if (single == null) await RunPostActionAsync();
    }

    /// <summary>由排程或第二個實例觸發：先倒數讓人有機會取消。</summary>
    public async Task StartScheduledAsync()
    {
        if (IsRunning || CountdownVisible) return;
        Logger.Write("收到排程啟動指令。");
        if (!await CountdownAsync("排程時間到，即將開始執行每日任務", 15))
        {
            Logger.Write("已取消這次的排程執行。", LogLevel.Warn);
            return;
        }
        await RunAsync();
    }

    public void Stop()
    {
        if (_runCts == null) return;
        CurrentText = "正在中止…";
        _runCts.Cancel();
    }

    public Task WaitIdleAsync() => _idle?.Task ?? Task.CompletedTask;

    private async Task RunPostActionAsync()
    {
        switch (Settings.PostAction)
        {
            case PostAction.CloseApp:
                if (await CountdownAsync("全部完成，即將關閉本程式", 10))
                    Application.Current.Shutdown();
                break;
            case PostAction.Sleep:
                if (await CountdownAsync("全部完成，電腦即將進入睡眠", 60))
                {
                    Logger.Write("進入睡眠。");
                    Power.Sleep();
                }
                break;
            case PostAction.Shutdown:
                if (await CountdownAsync("全部完成，電腦即將關機", 60))
                {
                    Logger.Write("執行關機。");
                    Power.Shutdown();
                }
                break;
        }
    }

    private async Task<bool> CountdownAsync(string title, int seconds)
    {
        _countdownCts = new CancellationTokenSource();
        CountdownTitle = title;
        CountdownVisible = true;
        try
        {
            for (var s = seconds; s > 0; s--)
            {
                CountdownSeconds = s;
                await Task.Delay(1000, _countdownCts.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            CountdownVisible = false;
            _countdownCts.Dispose();
            _countdownCts = null;
        }
    }

    private static LogLevel LevelOf(RunState s) => s switch
    {
        RunState.Success => LogLevel.Success,
        RunState.Warning or RunState.Incomplete or RunState.Stopped => LogLevel.Warn,
        RunState.Failed or RunState.Timeout => LogLevel.Error,
        _ => LogLevel.Info,
    };

#if DEBUG
    /// <summary>截圖測試用的假狀態。</summary>
    internal void ApplyDemoState()
    {
        IsRunning = true;
        // 先讓啟動時排進佇列的記錄（例如「尚未設定程式路徑」）加進來，再清掉
        _ui.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Logs.Clear();
        var notepad =Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        foreach (var t in Tasks) t.ExePath = notepad;
        Tasks[0].State = RunState.Success;
        Tasks[0].LastRunAt = DateTime.Now.AddMinutes(-26);
        Tasks[0].LastDurationSeconds = 1325;
        Tasks[0].LastResult = RunState.Success;
        Tasks[1].State = RunState.Running;
        Tasks[1].Elapsed = TimeSpan.FromSeconds(222);
        Tasks[1].LastRunAt = DateTime.Today.AddDays(-1).AddHours(5);
        Tasks[1].LastDurationSeconds = 1100;
        Tasks[1].LastResult = RunState.Success;
        if (Tasks.Count > 3) Tasks[3].ExePath = "";
        for (var i = 2; i < Tasks.Count; i++)
            if (Tasks[i].HasExe) Tasks[i].State = RunState.Queued;
        var count = Tasks.Count(t => t.HasExe);
        Progress = 1.0 / count;
        ProgressText = $"1 / {count}";
        CurrentText = $"正在執行　{Tasks[1].Name}（2 / {count}）";
        UpdateSummary();
        Logger.Write($"══ 開始執行每日任務，共 {count} 項 ══");
        Logger.Write($"▶ {Tasks[0].Name}");
        Logger.Write($"{Tasks[0].Name}：完成（22 分 5 秒）", LogLevel.Success);
        Logger.Write($"▶ {Tasks[1].Name}");
        Logger.Write("　追蹤子程式：python.exe");
    }
#endif
}
