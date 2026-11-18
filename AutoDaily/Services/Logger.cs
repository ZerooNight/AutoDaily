namespace AutoDaily.Services;

public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

public sealed record LogEntry(DateTime Time, string Message, LogLevel Level)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public static class Logger
{
    private static readonly object Gate = new();

    public static event Action<LogEntry>? Logged;

    public static string LogDir => Path.Combine(ConfigService.BaseDir, "logs");

    public static void Write(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(DateTime.Now, message, level);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(
                    Path.Combine(LogDir, $"{entry.Time:yyyy-MM-dd}.log"),
                    $"{entry.Time:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 寫不進記錄檔不影響執行
        }
        Logged?.Invoke(entry);
    }
}
