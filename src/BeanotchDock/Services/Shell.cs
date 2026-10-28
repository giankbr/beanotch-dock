using System.Runtime.InteropServices;

namespace BeanotchDock.Services;

// What the dock does to the rest of Windows: opens and brings forward apps, closes them, pins them, and looks after
// the Recycle Bin and the Downloads folder.
static partial class Shell
{
    // Apps group by their AppUserModelID when they have one (as the taskbar does), else by program path.
    public static string Key(string? aumid, string? path) =>
        aumid is { Length: > 0 } ? "id:" + aumid.ToLowerInvariant() : path is { Length: > 0 } ? "path:" + path.ToLowerInvariant() : "";

    public static string KeyFor(Pin pin) =>
        Key(pin.Aumid, pin.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Properties.ShortcutTarget(pin.Path) ?? pin.Path : pin.Path);

    private static readonly Dictionary<string, string?> storeNames = [];

    // A packaged app's own title ("Calculator"); null for desktop apps, which name themselves.
    public static string? StoreAppName(string aumid)
    {
        if (!aumid.Contains('!')) return null;
        if (!storeNames.TryGetValue(aumid, out var name)) storeNames[aumid] = name = Icons.DisplayName("shell:AppsFolder\\" + aumid);
        return name;
    }

    public static bool Launch(Pin pin)
    {
        if (pin.Path.Length > 0 && (File.Exists(pin.Path) || Directory.Exists(pin.Path))) return Native.Open(pin.Path);
        return pin.Aumid is { } id && Native.Open("shell:AppsFolder\\" + id);
    }

    // As on the Mac, a click brings the app forward: its front window, restored if it was minimized.
    public static void Activate(IEnumerable<nint> windows)
    {
        var list = windows.ToList();
        if (list.Count == 0) return;
        var front = list[0];
        if (Native.IsIconic(front)) Native.ShowWindow(front, 9); // SW_RESTORE, back to maximized if it was
        // A dialog the app has open in front of the window gets the focus instead.
        Native.SetForegroundWindow(Native.GetLastActivePopup(front) is var popup and not 0 ? popup : front);
    }

    // Windows' way: the app in front goes down to the taskbar's place (here, the dock).
    public static void Minimize(IEnumerable<nint> windows)
    {
        foreach (var w in windows) Native.PostMessage(w, 0x0112, 0xF020, 0); // WM_SYSCOMMAND, SC_MINIMIZE
    }

    // Asks each window to close, the way its own close button would.
    public static void Close(IEnumerable<nint> windows)
    {
        foreach (var w in windows) Native.PostMessage(w, 0x0010, 0, 0); // WM_CLOSE
    }

    public static Pin? PinFor(DockItem item) => item.Source is { } w
        ? new Pin { Name = item.Label, Path = w.Aumid is null ? w.Path ?? "" : "", Aumid = w.Aumid }
        : null;

    public static void Reveal(DockItem item)
    {
        var path = item.Pin?.Path is { Length: > 0 } p ? p : item.Source?.Path;
        if (path is not null) Native.Open("explorer.exe", $"/select,\"{path}\"");
    }

    public static void OpenWith(DockItem item, List<string> files)
    {
        var program = item.Source?.Path ?? (item.Pin?.Path is { Length: > 0 } p ? Properties.ShortcutTarget(p) ?? p : null);
        if (program is null || files.Count == 0) return;
        Native.Open(program, string.Join(" ", files.Select(f => $"\"{f}\"")));
    }

    // The shortcuts pinned to Windows' own taskbar, so the dock starts with the same apps; a few defaults otherwise.
    public static List<Pin> TaskbarPins()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        var pins = new List<Pin>();
        try
        {
            foreach (var link in Directory.GetFiles(folder, "*.lnk").OrderBy(File.GetCreationTimeUtc))
                pins.Add(new Pin { Name = Path.GetFileNameWithoutExtension(link), Path = link, Aumid = Properties.FileAumid(link) });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (pins.Count > 0) return pins;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            new Pin { Name = "File Explorer", Path = Path.Combine(windows, "explorer.exe"), Aumid = "Microsoft.Windows.Explorer" },
            new Pin { Name = "Microsoft Edge", Aumid = "MSEdge" },
            new Pin { Name = "Settings", Aumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" },
        ];
    }

    // MARK: Downloads

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    public static string DownloadsFolder
    {
        get
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, 0, out var p) != 0)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }

    public static void OpenDownloads() => Native.Open(DownloadsFolder);

    // MARK: Recycle Bin

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleInfo
    {
        public int Size;
        public long Bytes, Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? root, ref RecycleInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);

    public static bool TrashFull
    {
        get
        {
            var info = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 && info.Items > 0;
        }
    }

    public static void OpenTrash() => Native.Open("shell:RecycleBinFolder");

    // Windows asks before it empties, as Finder does.
    public static void EmptyTrash() => SHEmptyRecycleBin(0, null, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref FileOperation operation);

    // Into the Recycle Bin, so it can be put back.
    public static void Recycle(List<string> paths)
    {
        if (paths.Count == 0) return;
        var op = new FileOperation
        {
            Function = 3, // FO_DELETE
            From = string.Join("\0", paths) + "\0\0",
            Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        SHFileOperationW(ref op);
    }
}
