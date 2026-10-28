using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace BeanotchDock.Services;

// A top-level window that belongs on the taskbar (and so in the dock), with the app it groups under.
public sealed record TaskWindow(nint Handle, string Key, string AppName, string? Path, string? Aumid);

// The windows Windows itself would show on its taskbar, in front-to-back order, read fresh on each call. A window
// counts when it's visible, not cloaked (other virtual desktops, suspended Store apps), not a tool window, and either
// has no owner or asks for a taskbar button (WS_EX_APPWINDOW).
public sealed class Tasks(nint self)
{
    private readonly Dictionary<uint, (string? Path, string Name, string? Aumid)> processes = [];
    public List<TaskWindow> Windows()
    {
        var found = new List<TaskWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Belongs(hwnd) && Describe(hwnd) is { } window) found.Add(window);
            return true;
        }, 0);
        return found;
    }

    private bool Belongs(nint hwnd)
    {
        if (hwnd == self || !Native.IsWindowVisible(hwnd)) return false;
        if (Native.DwmGetWindowAttribute(hwnd, 14, out var cloaked, 4) == 0 && cloaked != 0) return false; // DWMWA_CLOAKED
        var ex = (long)Native.GetWindowLongPtr(hwnd, -20);
        bool appWindow = (ex & 0x40000) != 0, toolWindow = (ex & 0x80) != 0, noActivate = (ex & 0x08000000) != 0;
        if (toolWindow && !appWindow) return false;
        if (noActivate && !appWindow) return false;
        if (Native.GetWindow(hwnd, 4) != 0 && !appWindow) return false; // GW_OWNER
        // A tab an app took off the taskbar itself (ITaskbarList::DeleteTab).
        if (Native.GetProp(hwnd, "ITaskList_Deleted") != 0) return false;
        // The shell's own immersive windows (Start's frame, light-dismiss overlays, a frame with no app in it).
        if ((ex & 0x100) == 0 && Native.ClassName(hwnd) is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow"
            or "StartMenuSizingFrame" or "Shell_LightDismissOverlay") return false;
        return Native.Title(hwnd).Length > 0;
    }

    private TaskWindow? Describe(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (!processes.TryGetValue(pid, out var process))
        {
            var path = Native.ProcessPath(pid);
            processes[pid] = process = (path, path is null ? "" : AppName(path), Native.ProcessAumid(pid));
        }
        // The window's own ID first (it can split a browser's web apps from the browser), then its process's, which
        // Store apps have even when their windows don't say.
        var aumid = Properties.WindowAumid(hwnd) ?? process.Aumid;
        // A Store app's window belongs to ApplicationFrameHost; its own ID, set on the frame, says which app it is.
        if (aumid is null && process.Path is { } p && p.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var key = Shell.Key(aumid, process.Path);
        if (key.Length == 0) return null;
        var name = process.Name.Length > 0 ? process.Name : Native.Title(hwnd);
        if (aumid is not null && Shell.StoreAppName(aumid) is { } storeName) name = storeName;
        return new TaskWindow(hwnd, key, name, process.Path, aumid);
    }

    // An app's own name ("Google Chrome"), from its file description, else the program's file name.
    private static string AppName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public void Forget() => processes.Clear();
}

// Shell properties: the AppUserModelID Windows groups taskbar buttons by, for a window or a shortcut, and a shortcut's
// target.
static partial class Properties
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid format, uint id)
    {
        public Guid Format = format;
        public uint Id = id;
    }

    // A PROPVARIANT, enough of it to read strings (VT_LPWSTR).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Value;
        public nint Extra;
    }

    [GeneratedComInterface]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }

    private static readonly Guid PropertyStoreId = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly PropertyKey AppId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly PropertyKey LinkTarget = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint hwnd, in Guid riid, out nint store);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(string path, nint bindContext, int flags, in Guid riid, out nint store);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static string? WindowAumid(nint hwnd) =>
        SHGetPropertyStoreForWindow(hwnd, PropertyStoreId, out var store) == 0 ? Read(store, AppId) : null;

    public static string? FileAumid(string path) =>
        SHGetPropertyStoreFromParsingName(path, 0, 0, PropertyStoreId, out var store) == 0 ? Read(store, AppId) : null;

    public static string? ShortcutTarget(string path) =>
        SHGetPropertyStoreFromParsingName(path, 0, 0, PropertyStoreId, out var store) == 0 ? Read(store, LinkTarget) : null;

    private static string? Read(nint pointer, PropertyKey key)
    {
        if (pointer == 0) return null;
        try
        {
            var store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            store.GetValue(key, out var value);
            try { return value.Type == 31 && value.Value != 0 ? Marshal.PtrToStringUni(value.Value) : null; } // VT_LPWSTR
            finally { PropVariantClear(ref value); }
        }
        catch (COMException) { return null; }
        finally { Marshal.Release(pointer); }
    }
}
