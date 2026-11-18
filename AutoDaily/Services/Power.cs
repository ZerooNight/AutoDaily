using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoDaily.Services;

public static class Power
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>執行期間避免電腦自動睡眠。必須在 UI 執行緒呼叫。</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Shutdown() =>
        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
}
