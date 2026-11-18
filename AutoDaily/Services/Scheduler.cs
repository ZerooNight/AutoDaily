using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace AutoDaily.Services;

/// <summary>透過 Windows 工作排程器設定「每天定時自動執行」。</summary>
public static class Scheduler
{
    public const string TaskName = "AutoDaily 全自動每日";

    public static bool Exists() => Run($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static (bool Ok, string Message) Create(TimeSpan time, bool catchUp, bool wake)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AutoDaily.exe");
        var start = DateTime.Today.Add(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var user = WindowsIdentity.GetCurrent().Name;

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>全自動每日：依序執行各遊戲的每日任務</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>{(catchUp ? "true" : "false")}</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>{(wake ? "true" : "false")}</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>--autorun</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), "AutoDaily-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var code = Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out var output);
            return code == 0
                ? (true, $"已建立排程：每天 {time:hh\\:mm} 自動執行")
                : (false, $"建立排程失敗：{output.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, $"建立排程失敗：{ex.Message}");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static (bool Ok, string Message) Delete()
    {
        if (!Exists()) return (true, "未使用排程");
        var code = Run($"/Delete /TN \"{TaskName}\" /F", out var output);
        return code == 0 ? (true, "已移除每日排程") : (false, $"移除排程失敗：{output.Trim()}");
    }

    private static int Run(string args, out string output)
    {
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(15000);
        output = stdout.Result + stderr.Result;
        return p.HasExited ? p.ExitCode : -1;
    }
}
