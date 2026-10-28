using System.Runtime.InteropServices;

namespace BeanotchDock.Services;

// Windows' own taskbar, set to hide itself while the dock runs (it still slides up from the bottom edge) and put back
// as it was found when the dock quits. What it was is saved, so a crash or an uninstall can still restore it.
static partial class Taskbar
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public nint Window;
        public uint CallbackMessage, Edge;
        public Native.Rect Bounds;
        public nint Param;
    }

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? title);

    private const uint GetState = 4, SetState = 10;
    private const int AutoHide = 0x1, AlwaysOnTop = 0x2;

    private static AppBarData Data() => new() { Size = Marshal.SizeOf<AppBarData>(), Window = FindWindow("Shell_TrayWnd", null) };

    private static bool IsAutoHide()
    {
        var data = Data();
        return ((int)SHAppBarMessage(GetState, ref data) & AutoHide) != 0;
    }

    private static void Set(bool autoHide)
    {
        var data = Data();
        if (data.Window == 0) return;
        data.Param = autoHide ? AutoHide : AlwaysOnTop;
        SHAppBarMessage(SetState, ref data);
    }

    public static void Hide(DockSettings settings)
    {
        settings.TaskbarWasAutoHide ??= IsAutoHide();
        settings.Save();
        Set(true);
    }

    public static void Restore(DockSettings settings)
    {
        if (settings.TaskbarWasAutoHide is not { } was) return;
        Set(was);
        settings.TaskbarWasAutoHide = null;
        settings.Save();
    }
}
