using System.Diagnostics;
using System.Globalization;
using System.Windows;
using AutoDaily.Models;
using AutoDaily.Services;

namespace AutoDaily.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        _settings = settings;

        PostNone.IsChecked = settings.PostAction == PostAction.None;
        PostClose.IsChecked = settings.PostAction == PostAction.CloseApp;
        PostSleep.IsChecked = settings.PostAction == PostAction.Sleep;
        PostShutdown.IsChecked = settings.PostAction == PostAction.Shutdown;
        DelayBox.Text = settings.DelayBetweenSeconds.ToString(CultureInfo.InvariantCulture);
        ScheduleToggle.IsChecked = settings.ScheduleEnabled;
        TimeBox.Text = settings.ScheduleTime;
        CatchUpToggle.IsChecked = settings.ScheduleCatchUp;
        WakeToggle.IsChecked = settings.ScheduleWake;
        IgnoreBox.Text = settings.IgnoreProcesses;
        ConfigPathText.Text = ConfigService.ConfigPath;
        ConfigPathText.ToolTip = ConfigService.ConfigPath;

        var exists = Scheduler.Exists();
        ScheduleStatus.Text = exists
            ? $"目前狀態：已在工作排程器中建立「{Scheduler.TaskName}」"
            : "目前狀態：尚未建立排程";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        if (!int.TryParse(DelayBox.Text.Trim(), out var delay) || delay < 0 || delay > 3600)
        {
            ErrorText.Text = "等待秒數請輸入 0 ~ 3600";
            return;
        }

        var scheduleOn = ScheduleToggle.IsChecked == true;
        if (!TryParseTime(TimeBox.Text, out var time))
        {
            ErrorText.Text = "時間格式請輸入 HH:mm，例如 05:00";
            return;
        }

        var catchUp = CatchUpToggle.IsChecked == true;
        var wake = WakeToggle.IsChecked == true;
        var (ok, message) = scheduleOn ? Scheduler.Create(time, catchUp, wake) : Scheduler.Delete();
        if (!ok)
        {
            ErrorText.Text = message;
            return;
        }
        if (scheduleOn || _settings.ScheduleEnabled) Logger.Write(message, LogLevel.Success);

        _settings.PostAction =
            PostClose.IsChecked == true ? PostAction.CloseApp :
            PostSleep.IsChecked == true ? PostAction.Sleep :
            PostShutdown.IsChecked == true ? PostAction.Shutdown :
            PostAction.None;
        _settings.DelayBetweenSeconds = delay;
        _settings.ScheduleEnabled = scheduleOn;
        _settings.ScheduleTime = time.ToString(@"hh\:mm");
        _settings.ScheduleCatchUp = catchUp;
        _settings.ScheduleWake = wake;
        _settings.IgnoreProcesses = IgnoreBox.Text.Trim();
        DialogResult = true;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        var ok = TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out time);
        return ok && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigService.BaseDir}\"") { UseShellExecute = true });

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
