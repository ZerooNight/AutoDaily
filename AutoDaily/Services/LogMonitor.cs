using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AutoDaily.Services;

/// <summary>
/// 監看自動化工具的記錄檔，只看「這次執行期間新寫入」的內容，
/// 判斷是否出現完成訊息或代表沒完成的訊息，以及記錄檔是否太久沒更新（卡住）。
/// 以檔案 ID 追蹤檔案，工具把記錄檔改名輪替（例如 maafw.log → maafw.bak.xxx.log）時不會漏讀或重讀。
/// </summary>
public sealed class LogMonitor
{
    private sealed class FileState
    {
        public long Offset;
        public readonly Decoder Decoder = new UTF8Encoding(false).GetDecoder();
        public readonly StringBuilder Pending = new();
    }

    private sealed record Marker(string Text, Regex? Pattern)
    {
        /// <summary>符合時傳回位置，否則 -1。</summary>
        public int MatchIndex(string line)
        {
            if (Pattern == null) return line.IndexOf(Text, StringComparison.OrdinalIgnoreCase);
            var m = Pattern.Match(line);
            return m.Success ? m.Index : -1;
        }
    }

    private readonly List<(string Dir, string Pattern)> _specs;
    private readonly List<Marker> _success;
    private readonly List<Marker> _failure;
    private readonly Dictionary<(uint Volume, ulong Index), FileState> _files = new();
    private readonly Dictionary<string, (long Length, DateTime Write)> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _begun;

    private LogMonitor(List<(string, string)> specs, List<Marker> success, List<Marker> failure)
    {
        _specs = specs;
        _success = success;
        _failure = failure;
        LastActivityUtc = DateTime.UtcNow;
    }

    public bool Configured => _specs.Count > 0;
    public bool HasSuccessMarkers => _success.Count > 0;
    public bool AnyFileSeen { get; private set; }
    public string? SuccessLine { get; private set; }
    public string? FailureLine { get; private set; }
    public DateTime LastActivityUtc { get; private set; }
    public TimeSpan IdleFor => DateTime.UtcNow - LastActivityUtc;

    public static LogMonitor Create(string? logPaths, string? successMarkers, string? failureMarkers, string toolDir) =>
        new(ParseSpecs(logPaths, toolDir), ParseMarkers(successMarkers), ParseMarkers(failureMarkers));

    /// <summary>啟動工具前呼叫：記下目前每個記錄檔的大小，之前的內容一律不看。</summary>
    public void Begin()
    {
        Scan();
        _begun = true;
        LastActivityUtc = DateTime.UtcNow;
    }

    /// <summary>讀取新寫入的內容並比對訊息。</summary>
    public void Poll()
    {
        if (!_begun) Begin();
        Scan();
    }

    /// <summary>執行結束後呼叫：把最後一行（沒有換行結尾）也拿來比對。</summary>
    public void Finish()
    {
        Poll();
        foreach (var st in _files.Values)
        {
            if (st.Pending.Length == 0) continue;
            Inspect(st.Pending.ToString());
            st.Pending.Clear();
        }
    }

    /// <summary>給設定畫面用：列出目前符合設定的記錄檔（新的在前）。</summary>
    public static List<FileInfo> ResolveFiles(string? logPaths, string toolDir) =>
        ParseSpecs(logPaths, toolDir)
            .Where(s => Directory.Exists(s.Dir))
            .SelectMany(s => SafeEnumerate(s.Dir, s.Pattern))
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

    private void Scan()
    {
        foreach (var (dir, pattern) in _specs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in SafeEnumerate(dir, pattern))
            {
                try
                {
                    ScanFile(path);
                }
                catch (IOException)
                {
                    // 檔案正被輪替或刪除，下一輪再看
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private void ScanFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;
        var length = info.Length;
        var write = info.LastWriteTimeUtc;

        // 大小與修改時間都沒變就不必開檔
        if (_seenPaths.TryGetValue(path, out var seen) && seen.Length == length && seen.Write == write) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var key = GetFileKey(fs.SafeFileHandle);
        _seenPaths[path] = (length, write);
        AnyFileSeen = true;

        if (!_files.TryGetValue(key, out var st))
        {
            // 開始前就存在的檔案：從目前結尾開始看；開始後才出現的新檔：從頭看
            st = new FileState { Offset = _begun ? 0 : length };
            _files[key] = st;
            if (!_begun) return;
        }

        length = fs.Length;
        if (length < st.Offset)
        {
            // 被清空重寫
            st.Offset = 0;
            st.Pending.Clear();
            st.Decoder.Reset();
        }
        if (length == st.Offset) return;

        fs.Seek(st.Offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            st.Offset += read;
            var count = st.Decoder.GetChars(buffer, 0, read, chars, 0);
            st.Pending.Append(chars, 0, count);
            DrainLines(st);
        }
        LastActivityUtc = DateTime.UtcNow;
    }

    private void DrainLines(FileState st)
    {
        var text = st.Pending.ToString();
        var start = 0;
        int newline;
        while ((newline = text.IndexOf('\n', start)) >= 0)
        {
            Inspect(text[start..newline]);
            start = newline + 1;
        }
        st.Pending.Clear();
        if (start < text.Length) st.Pending.Append(text, start, text.Length - start);
    }

    private void Inspect(string line)
    {
        line = line.TrimEnd('\r').TrimStart('﻿');
        if (line.Length == 0) return;
        if (FailureLine == null && FirstMatch(_failure, line) is var f and >= 0) FailureLine = Clip(line, f);
        if (SuccessLine == null && FirstMatch(_success, line) is var s and >= 0) SuccessLine = Clip(line, s);
    }

    private static int FirstMatch(List<Marker> markers, string line)
    {
        foreach (var m in markers)
            if (m.MatchIndex(line) is var i and >= 0) return i;
        return -1;
    }

    /// <summary>長行（例如 MaaFramework 的事件記錄）從符合處附近開始截取，才看得到重點。</summary>
    private static string Clip(string line, int matchIndex)
    {
        const int max = 120;
        var start = matchIndex > max - 40 ? matchIndex - 10 : 0;
        var text = line[start..].Trim();
        if (text.Length > max) text = text[..max] + "…";
        return start > 0 ? "…" + text : text;
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>每行一個路徑，相對路徑以工具所在資料夾為準；檔名可用 * ? 萬用字元。</summary>
    private static List<(string Dir, string Pattern)> ParseSpecs(string? text, string toolDir)
    {
        var list = new List<(string, string)>();
        foreach (var raw in SplitLines(text))
        {
            var p = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
            if (!Path.IsPathRooted(p))
            {
                if (string.IsNullOrWhiteSpace(toolDir)) continue;
                p = Path.GetFullPath(Path.Combine(toolDir, p));
            }
            if (Directory.Exists(p))
                list.Add((p, "*"));
            else if (Path.GetDirectoryName(p) is { Length: > 0 } dir)
                list.Add((dir, Path.GetFileName(p)));
        }
        return list;
    }

    /// <summary>每行一個訊息；以 re: 開頭的是正規表示式，# 開頭是註解。</summary>
    private static List<Marker> ParseMarkers(string? text)
    {
        var list = new List<Marker>();
        foreach (var line in SplitLines(text))
        {
            if (line.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var re = new Regex(line[3..], RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                    list.Add(new Marker(line, re));
                }
                catch (ArgumentException ex)
                {
                    Logger.Write($"　正規表示式有誤，已略過：{line}（{ex.Message}）", LogLevel.Warn);
                }
            }
            else
            {
                list.Add(new Marker(line, null));
            }
        }
        return list;
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "")
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'));

    // ───── 檔案 ID ─────

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    private static (uint, ulong) GetFileKey(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("無法取得檔案資訊");
        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }
}
