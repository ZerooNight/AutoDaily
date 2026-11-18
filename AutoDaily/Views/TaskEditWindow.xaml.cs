using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AutoDaily.Models;
using AutoDaily.Services;
using Microsoft.Win32;

namespace AutoDaily.Views;

public partial class TaskEditWindow : Window
{
    private static readonly string[] Swatches =
    {
        "#5AB4F5", "#F5C842", "#3DDBA4", "#B18CFF", "#FF7A90", "#FF9F43", "#7C6CFF", "#A3ABBD",
    };

    private readonly GameTask _task;
    private readonly DispatcherTimer _logCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _emulatorTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _emulatorQuery;

    public bool Deleted { get; private set; }

    public TaskEditWindow(GameTask task, bool isNew)
    {
        InitializeComponent();
        MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        _task = task;
        task.KeepCursorInGame ??= false;
        DataContext = task;
        TitleText.Text = isNew ? "新增任務" : "編輯任務";
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        BuildPresets();
        BuildSwatches();

        _logCheckTimer.Tick += (_, _) =>
        {
            _logCheckTimer.Stop();
            UpdateLogFilesStatus();
        };
        _emulatorTimer.Tick += async (_, _) =>
        {
            _emulatorTimer.Stop();
            await RefreshEmulatorsAsync();
        };
        task.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(GameTask.ExePath):
                    ScheduleLogCheck();
                    break;
                case nameof(GameTask.StartEmulator):
                    if (_task.StartEmulator && string.IsNullOrWhiteSpace(_task.EmulatorPath))
                        _task.EmulatorPath = MuMu.FindManager() ?? "";
                    ScheduleEmulatorRefresh();
                    break;
                case nameof(GameTask.EmulatorPath):
                    ScheduleEmulatorRefresh();
                    break;
            }
        };
        UpdateLogFilesStatus();
        _ = RefreshEmulatorsAsync();
    }

    private void ScheduleEmulatorRefresh()
    {
        _emulatorTimer.Stop();
        _emulatorTimer.Start();
    }

    /// <summary>向 MuMu 查詢有哪些模擬器，列出來讓使用者選要開哪一台。</summary>
    private async Task RefreshEmulatorsAsync()
    {
        if (!_task.StartEmulator) return;
        var query = ++_emulatorQuery;

        var manager = MuMu.ResolveManager(_task.EmulatorPath);
        if (manager == null)
        {
            BuildEmulatorChoices(Array.Empty<MuMuPlayer>());
            SetEmulatorStatus("⚠ 找不到 MuMuManager.exe，請按「瀏覽…」選擇 MuMu 安裝資料夾裡的 MuMuManager.exe", "WarnBrush");
            return;
        }

        SetEmulatorStatus("正在向 MuMu 查詢模擬器…", "TextMuteBrush");
        var (players, error) = await MuMu.QueryAllAsync(manager);
        if (query != _emulatorQuery) return; // 已經有更新的查詢

        BuildEmulatorChoices(players);
        if (players.Count == 0)
            SetEmulatorStatus($"⚠ 查不到模擬器：{error ?? "MuMu 沒有回傳任何模擬器"}", "WarnBrush");
        else if (players.All(p => p.Index != _task.EmulatorIndex))
            SetEmulatorStatus($"⚠ MuMu 裡沒有編號 {_task.EmulatorIndex} 的模擬器，請重新選擇", "WarnBrush");
        else
            SetEmulatorStatus($"✓ 找到 {players.Count} 台模擬器（{manager}）", "SuccessBrush");
    }

    private void BuildEmulatorChoices(IReadOnlyList<MuMuPlayer> players)
    {
        EmulatorPanel.Children.Clear();
        var choices = players.Select(p => (p.Index, Text: $"#{p.Index}  {p.Name} · {p.StateText}")).ToList();
        if (choices.All(c => c.Index != _task.EmulatorIndex))
            choices.Insert(0, (_task.EmulatorIndex, $"#{_task.EmulatorIndex}"));

        foreach (var (index, text) in choices)
        {
            var radio = new RadioButton
            {
                Content = text,
                GroupName = "emulator",
                Style = (Style)FindResource("SegmentRadio"),
                IsChecked = index == _task.EmulatorIndex,
            };
            radio.Checked += (_, _) => _task.EmulatorIndex = index;
            EmulatorPanel.Children.Add(radio);
        }
    }

    private void SetEmulatorStatus(string text, string brushKey)
    {
        EmulatorStatus.Text = text;
        EmulatorStatus.Foreground = (Brush)FindResource(brushKey);
    }

    private void BrowseEmulator_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇 MuMuManager.exe",
            Filter = "MuMuManager.exe|MuMuManager.exe|程式 (*.exe)|*.exe",
        };
        var current = TaskRunner.CleanPath(_task.EmulatorPath);
        var start = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
            start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netease");
        if (Directory.Exists(start)) dialog.InitialDirectory = start;

        if (dialog.ShowDialog(this) == true) _task.EmulatorPath = dialog.FileName;
    }

    private void LogPaths_TextChanged(object sender, TextChangedEventArgs e) => ScheduleLogCheck();

    private void ScheduleLogCheck()
    {
        _logCheckTimer.Stop();
        _logCheckTimer.Start();
    }

    /// <summary>顯示目前設定找得到哪些記錄檔，方便確認路徑有沒有填對。</summary>
    private void UpdateLogFilesStatus()
    {
        if (string.IsNullOrWhiteSpace(_task.LogPaths))
        {
            LogFilesStatus.Text = "未設定記錄檔，不做完成檢查。";
            LogFilesStatus.Foreground = (Brush)FindResource("TextMuteBrush");
            return;
        }

        var exe = TaskRunner.CleanPath(_task.ExePath);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            LogFilesStatus.Text = "先設定程式路徑，才能找到記錄檔。";
            LogFilesStatus.Foreground = (Brush)FindResource("TextMuteBrush");
            return;
        }

        var files = LogMonitor.ResolveFiles(_task.LogPaths, Path.GetDirectoryName(exe)!);
        if (files.Count == 0)
        {
            LogFilesStatus.Text = "⚠ 目前找不到符合的記錄檔（工具還沒跑過也會這樣，跑過一次後再確認）";
            LogFilesStatus.Foreground = (Brush)FindResource("WarnBrush");
            return;
        }

        var newest = files[0];
        var when = newest.LastWriteTime.Date == DateTime.Today ? $"今天 {newest.LastWriteTime:HH:mm}" : newest.LastWriteTime.ToString("M/d HH:mm");
        LogFilesStatus.Text = $"✓ 找到 {files.Count} 個記錄檔，最新的是 {newest.Name}（{when} 更新）";
        LogFilesStatus.Foreground = (Brush)FindResource("SuccessBrush");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    private void BuildPresets()
    {
        foreach (var preset in Presets.All)
        {
            var button = new Button
            {
                Content = preset.ShortName,
                Style = (Style)FindResource("PresetChip"),
                Tag = preset,
            };
            button.Click += (_, _) =>
            {
                Presets.Apply(_task, preset);
                BuildSwatches();
            };
            PresetPanel.Children.Add(button);
        }
    }

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        var colors = Swatches.Contains(_task.Color, StringComparer.OrdinalIgnoreCase)
            ? Swatches
            : Swatches.Append(_task.Color).ToArray();
        foreach (var hex in colors)
        {
            var radio = new RadioButton
            {
                GroupName = "swatch",
                Style = (Style)FindResource("SwatchRadio"),
                Background = (Brush)new BrushConverter().ConvertFromString(hex)!,
                IsChecked = string.Equals(hex, _task.Color, StringComparison.OrdinalIgnoreCase),
                ToolTip = hex,
            };
            radio.Checked += (_, _) => _task.Color = hex;
            SwatchPanel.Children.Add(radio);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇自動化程式",
            Filter = "程式 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|所有檔案 (*.*)|*.*",
        };
        var current = TaskRunner.CleanPath(_task.ExePath);
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(Path.GetDirectoryName(current)))
            dialog.InitialDirectory = Path.GetDirectoryName(current);

        if (dialog.ShowDialog(this) != true) return;
        _task.ExePath = dialog.FileName;

        // 新任務選了認得的工具，就順便帶入預設參數
        if (string.IsNullOrWhiteSpace(_task.Arguments) && Presets.Guess(dialog.FileName) is { } preset)
        {
            Presets.Apply(_task, preset);
            BuildSwatches();
        }
        else if (string.IsNullOrWhiteSpace(_task.ToolName))
        {
            _task.ToolName = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_task.Name))
        {
            ErrorText.Text = "請輸入名稱";
            return;
        }
        if (_task.TimeoutMinutes < 0) _task.TimeoutMinutes = 0;
        if (_task.GameStartTimeoutMinutes < 0) _task.GameStartTimeoutMinutes = 0;
        _task.MaxRetries = Math.Clamp(_task.MaxRetries, 0, 5);
        if (_task.LogStallMinutes < 0) _task.LogStallMinutes = 0;
        _task.LogPaths ??= "";
        _task.SuccessMarkers ??= "";
        _task.FailureMarkers ??= "";
        if (string.IsNullOrWhiteSpace(_task.Badge)) _task.Badge = _task.Name.Trim()[..1];
        _task.Name = _task.Name.Trim();
        _task.ExePath = _task.ExePath.Trim().Trim('"');
        _task.EmulatorPath = (_task.EmulatorPath ?? "").Trim().Trim('"');
        if (_task.EmulatorIndex < 0) _task.EmulatorIndex = 0;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Show(this, "刪除任務", $"確定要刪除「{_task.Name}」嗎？", "刪除", "取消", danger: true)) return;
        Deleted = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
