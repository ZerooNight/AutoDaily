using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoDaily.Services;

/// <summary>
/// 追蹤自動化工具「以及它開出來的所有子程式」何時全部結束。
/// 有些工具的 exe 只是啟動器（開完 python 就自己關掉），只等主程式結束會太早判定完成。
/// 主要用 Job Object 收集整棵程式樹；Job 無法使用時退回用父程序 ID 追蹤。
/// 被列為「忽略」的程式（遊戲本體、常駐的 adb 等）與其子程式不需要等它結束。
/// </summary>
public sealed class ProcessTreeWatcher : IDisposable
{
    private sealed class Node
    {
        public int Pid;
        public int ParentPid;
        public string Name = "";
        public long Created;
        public bool Ignored;
        public bool Dead;
    }

    private readonly Process _root;
    private readonly int _rootPid;
    private readonly bool _trackChildren;
    private readonly HashSet<string> _ignore;
    private readonly Dictionary<int, Node> _nodes = new();
    private IntPtr _job;
    private readonly bool _useJob;
    private Dictionary<int, (int ParentPid, string Name)> _lastSnapshot = new();

    public event Action<string>? ChildDiscovered;

    public ProcessTreeWatcher(Process root, string rootName, IEnumerable<string> ignoreNames, bool trackChildren)
    {
        _root = root;
        _rootPid = root.Id;
        _trackChildren = trackChildren;
        _ignore = new HashSet<string>(ignoreNames, StringComparer.OrdinalIgnoreCase);

        if (trackChildren)
        {
            _job = Native.CreateJob();
            if (_job != IntPtr.Zero)
            {
                try { _useJob = Native.AssignProcessToJobObject(_job, root.Handle); }
                catch { _useJob = false; }
                if (!_useJob)
                {
                    Native.CloseHandle(_job);
                    _job = IntPtr.Zero;
                }
            }
        }

        _nodes[_rootPid] = new Node { Pid = _rootPid, Name = rootName, Created = Native.GetCreationTime(_rootPid) };
    }

    public bool AnyActive => _nodes.Values.Any(n => !n.Dead && !n.Ignored);

    public IEnumerable<string> ActiveNames =>
        _nodes.Values.Where(n => !n.Dead && !n.Ignored).Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    public int? RootExitCode
    {
        get
        {
            try { return _root.HasExited ? _root.ExitCode : null; }
            catch { return null; }
        }
    }

    public void Poll()
    {
        // 先取 Job 清單再拍快照：Job 裡的程式一定會出現在之後的快照中（除非剛好結束）
        var jobPids = _useJob ? Native.QueryJobProcessIds(_job) : null;
        var snapshot = Native.SnapshotProcesses();
        _lastSnapshot = snapshot;

        foreach (var n in _nodes.Values)
        {
            if (n.Dead) continue;
            bool alive;
            if (n.Pid == _rootPid)
                alive = RootAlive(snapshot);
            else if (jobPids != null)
                alive = jobPids.Contains(n.Pid);
            else
                alive = snapshot.TryGetValue(n.Pid, out var e)
                        && string.Equals(e.Name, n.Name, StringComparison.OrdinalIgnoreCase)
                        && SameProcess(n);
            if (!alive) n.Dead = true;
        }

        if (!_trackChildren) return;

        var added = new List<Node>();
        if (jobPids != null)
        {
            foreach (var pid in jobPids)
            {
                // 還查不到名稱就等下一輪，避免把遊戲誤判成要等待的程式
                if (_nodes.ContainsKey(pid) || !snapshot.TryGetValue(pid, out var e)) continue;
                added.Add(Add(pid, e.ParentPid, e.Name, Native.GetCreationTime(pid)));
            }
        }
        else
        {
            bool found;
            do
            {
                found = false;
                foreach (var (pid, e) in snapshot)
                {
                    if (pid <= 4 || _nodes.ContainsKey(pid) || !_nodes.TryGetValue(e.ParentPid, out var parent)) continue;
                    var created = Native.GetCreationTime(pid);
                    // 父程序 ID 可能被重複使用：子程式不可能比父程式更早建立
                    if (created != 0 && parent.Created != 0 && created < parent.Created) continue;
                    added.Add(Add(pid, e.ParentPid, e.Name, created));
                    found = true;
                }
            } while (found);
        }

        if (added.Count == 0) return;
        PropagateIgnored();
        foreach (var n in added.Where(n => !n.Ignored))
            ChildDiscovered?.Invoke(n.Name);
    }

    /// <summary>在上一次快照的整個系統中，找出第一個符合名稱的程式（不限於本工具開的）。</summary>
    public string? FindRunning(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return null;
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var (_, e) in _lastSnapshot)
            if (set.Contains(e.Name)) return e.Name;
        return null;
    }

    /// <summary>結束仍在執行的工具程式（不包含遊戲等被忽略的程式）。</summary>
    public void KillTools()
    {
        // 先關主程式，避免它又開出新的子程式
        foreach (var n in _nodes.Values.Where(n => !n.Dead && !n.Ignored).OrderBy(n => n.Pid == _rootPid ? 0 : 1).ToList())
        {
            try
            {
                if (n.Pid == _rootPid)
                {
                    _root.Kill();
                    _root.WaitForExit(3000);
                    continue;
                }
                using var p = Process.GetProcessById(n.Pid);
                if (!string.Equals(p.ProcessName + ".exe", n.Name, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
                // 已經結束或無權限，略過
            }
        }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            Native.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    private Node Add(int pid, int parentPid, string name, long created)
    {
        var node = new Node
        {
            Pid = pid,
            ParentPid = parentPid,
            Name = name,
            Created = created,
            Ignored = _ignore.Contains(name),
        };
        _nodes[pid] = node;
        return node;
    }

    /// <summary>被忽略程式（例如遊戲）開出來的子程式也一併忽略。</summary>
    private void PropagateIgnored()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var n in _nodes.Values)
            {
                if (n.Ignored || n.Pid == _rootPid || n.ParentPid == n.Pid) continue;
                if (_nodes.TryGetValue(n.ParentPid, out var p) && p.Ignored)
                {
                    n.Ignored = true;
                    changed = true;
                }
            }
        } while (changed);
    }

    private bool RootAlive(Dictionary<int, (int ParentPid, string Name)> snapshot)
    {
        try { return !_root.HasExited; }
        catch { return snapshot.ContainsKey(_rootPid); }
    }

    private static bool SameProcess(Node n)
    {
        if (n.Created == 0) return true;
        var now = Native.GetCreationTime(n.Pid);
        return now == 0 || now == n.Created;
    }
}

internal static class Native
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JobExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, int length, IntPtr returnLength);

    public static Dictionary<int, (int ParentPid, string Name)> SnapshotProcesses()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero) return map;
        try
        {
            var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref e)) return map;
            do
            {
                map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile);
            } while (Process32Next(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    /// <summary>取得程式建立時間（FILETIME），失敗傳回 0。</summary>
    public static long GetCreationTime(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return GetProcessTimes(h, out var created, out _, out _, out _) ? created : 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 建立 Job：允許子程式自行脫離（避免某些程式因為無法脫離而啟動失敗），
    /// 不設定「關閉時結束全部」，所以本程式關閉不會連帶關掉遊戲。
    /// </summary>
    public static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitBreakawayOk;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JobExtendedLimitInformation>());
        return job;
    }

    public static HashSet<int>? QueryJobProcessIds(IntPtr job)
    {
        const int capacity = 1024;
        var size = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero)) return null;
            var count = Marshal.ReadInt32(buffer, 4);
            var set = new HashSet<int>(count);
            for (var i = 0; i < count; i++)
                set.Add((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
            return set;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
