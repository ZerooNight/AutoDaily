using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoDaily.Services;

/// <summary>
/// 遊戲在最前面時，把跑到遊戲視窗外的滑鼠游標移回遊戲中央。
/// BetterGI 有些步驟會送出真的滑鼠點擊（點在游標所在位置，例如地脈花領獎前按中鍵重置視角），
/// 遊戲是視窗模式時游標若停在外面，就會點到桌面或其他視窗，遊戲因此失去焦點而暫停。
/// 只在遊戲本來就是前景視窗時動作，不會去搶焦點。
/// </summary>
public sealed class CursorKeeper
{
    public readonly record struct CursorCheck(IntPtr Window, int Left, int Top, int Right, int Bottom, int CursorX, int CursorY, bool Outside);

    private readonly HashSet<string> _names;
    private readonly Dictionary<uint, bool> _pidIsGame = new();

    public CursorKeeper(IEnumerable<string> exeNames)
    {
        _names = new HashSet<string>(exeNames.Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);
    }

    public int MoveCount { get; private set; }

    /// <summary>檢查目前前景視窗是不是遊戲、游標是否在它的畫面區域外（不移動游標）。</summary>
    public CursorCheck? Inspect()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (!IsGame(pid)) return null;

        if (!GetClientRect(hwnd, out var rc) || rc.Right <= 0 || rc.Bottom <= 0) return null;
        var origin = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return null;
        if (!GetCursorPos(out var cursor)) return null;

        int left = origin.X, top = origin.Y, right = origin.X + rc.Right, bottom = origin.Y + rc.Bottom;
        var outside = cursor.X < left || cursor.X >= right || cursor.Y < top || cursor.Y >= bottom;
        return new CursorCheck(hwnd, left, top, right, bottom, cursor.X, cursor.Y, outside);
    }

    /// <summary>游標在遊戲畫面外就移回中央；有移動時傳回 true。</summary>
    public bool Tick()
    {
        if (Inspect() is not { Outside: true } c) return false;
        if (!SetCursorPos((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2)) return false;
        MoveCount++;
        return true;
    }

    /// <summary>目前前景視窗所屬程式的名稱（自我測試用）。</summary>
    internal static string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private bool IsGame(uint pid)
    {
        if (_pidIsGame.TryGetValue(pid, out var isGame)) return isGame;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            isGame = _names.Contains(p.ProcessName);
        }
        catch
        {
            isGame = false;
        }
        _pidIsGame[pid] = isGame;
        return isGame;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
