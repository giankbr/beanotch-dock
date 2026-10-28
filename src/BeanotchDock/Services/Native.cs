using System.Runtime.InteropServices;
using Windows.Graphics;

namespace BeanotchDock.Services;

// The Win32 calls the dock needs for its own window: a bare popup, a shape cut to what's drawn, the pointer, and a
// few facts about other windows.
static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    // A bare popup that never takes the focus from the app in front and stays off Alt-Tab: no frame, no rounded
    // corners, no border.
    internal static void Frameless(nint hwnd)
    {
        const long caption = 0x00C00000, thickFrame = 0x00040000, sysMenu = 0x00080000, minBox = 0x00020000, maxBox = 0x00010000, popup = 0x80000000;
        var style = (long)GetWindowLongPtr(hwnd, -16);
        SetWindowLongPtr(hwnd, -16, (nint)((style & ~(caption | thickFrame | sysMenu | minBox | maxBox)) | popup));
        var ex = (long)GetWindowLongPtr(hwnd, -20);
        // Drops the frame edges; adds WS_EX_TOOLWINDOW (off the taskbar and Alt-Tab) and WS_EX_NOACTIVATE.
        SetWindowLongPtr(hwnd, -20, (nint)((ex & ~(0x1L | 0x100L | 0x200L | 0x20000L)) | 0x80L | 0x08000000L));
        uint square = 1, noBorder = 0xFFFFFFFE;
        DwmSetWindowAttribute(hwnd, 33, ref square, 4);
        DwmSetWindowAttribute(hwnd, 34, ref noBorder, 4);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0010);
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint destination, nint first, nint second, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    // The window's shape: the bar (the first rectangle, with rounded corners) and whatever sticks out above it. The
    // system owns the region once it's set.
    internal static void Shape(nint hwnd, List<RectInt32> parts, int radius)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        for (var i = 0; i < parts.Count; i++)
        {
            var r = parts[i];
            var part = i == 0
                ? CreateRoundRectRgn(r.X, r.Y, r.X + r.Width + 1, r.Y + r.Height + 1, radius * 2, radius * 2)
                : CreateRectRgn(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
            CombineRgn(region, region, part, 2); // RGN_OR
            DeleteObject(part);
        }
        SetWindowRgn(hwnd, region, true);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    internal static Point? Cursor() => GetCursorPos(out var p) ? p : null;

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    internal static nint Foreground() => GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] text, int max);

    internal static string Title(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint hwnd, [Out] char[] name, int max);

    internal static string ClassName(nint hwnd)
    {
        var buffer = new char[128];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetProp(nint hwnd, string name);

    [LibraryImport("user32.dll")]
    internal static partial nint GetLastActivePopup(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint process);

    internal delegate bool EnumProc(nint hwnd, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumProc callback, nint data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(nint parent, EnumProc callback, nint data);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetApplicationUserModelId(nint process, ref uint length, [Out] char[] id);

    // A packaged (Store) process's AppUserModelID; null for ordinary programs.
    internal static string? ProcessAumid(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = new char[130];
            var length = (uint)buffer.Length;
            return GetApplicationUserModelId(process, ref length, buffer) == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)) : null;
        }
        finally { CloseHandle(process); }
    }

    // The program a process runs, readable even for elevated ones (PROCESS_QUERY_LIMITED_INFORMATION).
    internal static string? ProcessPath(uint id)
    {
        var process = OpenProcess(0x1000, false, id);
        if (process == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecute(nint hwnd, string? verb, string file, string? parameters, string? directory, int show);

    // Opens a file, a shortcut, a folder or a shell: location; false when Windows couldn't.
    internal static bool Open(string target, string? parameters = null) => ShellExecute(0, null, target, parameters, null, 1) > 32;
}
