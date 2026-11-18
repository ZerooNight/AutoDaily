using System.Text;
using System.Windows;
using System.Windows.Threading;
using AutoDaily.Services;
using AutoDaily.ViewModels;
using AutoDaily.Views;

namespace AutoDaily;

public partial class App : Application
{
    private const string MutexName = @"Local\AutoDaily.SingleInstance";
    private const string RunSignalName = @"Local\AutoDaily.RunSignal";
    private const string ShowSignalName = @"Local\AutoDaily.ShowSignal";

    private Mutex? _mutex;
    private EventWaitHandle? _runSignal;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var autorun = e.Args.Any(a => a.Equals("--autorun", StringComparison.OrdinalIgnoreCase));

#if DEBUG
        if (DebugSnapshot.TryRun(e.Args)) return;
#endif

        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // 已經開著：把「開始執行」或「顯示視窗」交給原本的視窗
            try
            {
                using var signal = EventWaitHandle.OpenExisting(autorun ? RunSignalName : ShowSignalName);
                signal.Set();
            }
            catch
            {
                // 原本的實例可能正在關閉
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Write($"未預期的錯誤：{args.Exception.Message}", LogLevel.Error);
            args.Handled = true;
        };

        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        _runSignal = new EventWaitHandle(false, EventResetMode.AutoReset, RunSignalName);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _runSignal, _showSignal };
            while (true)
            {
                var index = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    window.BringToFront();
                    if (index == 0) _ = vm.StartScheduledAsync();
                });
            }
        })
        { IsBackground = true, Name = "AutoDaily signal listener" };
        listener.Start();

        if (autorun)
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = vm.StartScheduledAsync());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Power.KeepAwake(false);
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
