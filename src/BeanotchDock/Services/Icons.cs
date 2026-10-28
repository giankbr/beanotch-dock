using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BeanotchDock.Services;

// Large icons (256 px, so magnified ones stay sharp) from the shell, the way Explorer draws them: for a program or a
// shortcut, a Store app by its ID, the Recycle Bin (full or empty) and the Downloads folder.
static partial class Icons
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Size(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [GeneratedComInterface]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    internal partial interface IShellItemImageFactory
    {
        void GetImage(Size size, int flags, out nint bitmap);
    }

    [GeneratedComInterface]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal partial interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);
        void GetParent(out nint parent);
        void GetDisplayName(uint form, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(nint other, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public short Planes, BitsPixel;
        public nint Bits;
    }

    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Dictionary<string, ImageSource> Cache = [];
    public const string TrashPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid, out nint item);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObject(nint handle, int size, out Bitmap bitmap);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    // Where the shell finds each kind of item.
    private static string? Source(DockItem item) => item.Kind switch
    {
        ItemKind.Trash => TrashPath,
        ItemKind.Downloads => Shell.DownloadsFolder,
        _ => item.Pin is { Path.Length: > 0 } pin && (File.Exists(pin.Path) || Directory.Exists(pin.Path)) ? pin.Path
            : item.Source?.Path is { } path && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && item.Source.Aumid is null ? path
            : (item.Pin?.Aumid ?? item.Source?.Aumid) is { } aumid ? "shell:AppsFolder\\" + aumid
            : item.Source?.Path,
    };

    public static Task<ImageSource?> ForAsync(DockItem item)
    {
        if (Source(item) is not { } source) return Task.FromResult<ImageSource?>(null);
        // The Recycle Bin's picture changes with what's in it, so it's never kept.
        if (item.Kind != ItemKind.Trash && Cache.TryGetValue(source, out var known)) return Task.FromResult<ImageSource?>(known);
        var image = Load(source);
        if (image is not null && item.Kind != ItemKind.Trash) Cache[source] = image;
        return Task.FromResult(image);
    }

    private static ImageSource? Load(string source)
    {
        if (SHCreateItemFromParsingName(source, 0, ImageFactoryId, out var pointer) != 0 || pointer == 0) return null;
        nint hbitmap = 0;
        try
        {
            var factory = (IShellItemImageFactory)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            factory.GetImage(new Size(256, 256), 0x1 | 0x4, out hbitmap); // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
            return hbitmap == 0 ? null : ToImage(hbitmap);
        }
        catch (COMException) { return null; }
        finally
        {
            if (hbitmap != 0) Native.DeleteObject(hbitmap);
            Marshal.Release(pointer);
        }
    }

    // The shell's bitmap as top-down BGRA with premultiplied alpha, which is what a WriteableBitmap holds.
    private static WriteableBitmap? ToImage(nint hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<Bitmap>(), out var bmp) == 0 || bmp.Width <= 0 || bmp.Height <= 0) return null;
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = bmp.Width, Height = -bmp.Height, Planes = 1, BitCount = 32 };
        var pixels = new byte[bmp.Width * bmp.Height * 4];
        var dc = GetDC(0);
        try { if (GetDIBits(dc, hbitmap, 0, (uint)bmp.Height, pixels, ref header, 0) == 0) return null; }
        finally { ReleaseDC(0, dc); }
        var image = new WriteableBitmap(bmp.Width, bmp.Height);
        using (var stream = image.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        image.Invalidate();
        return image;
    }

    // The name the shell shows for an item: a Store app's title, say.
    public static string? DisplayName(string source)
    {
        if (SHCreateItemFromParsingName(source, 0, ShellItemId, out var pointer) != 0 || pointer == 0) return null;
        try
        {
            var item = (IShellItem)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            item.GetDisplayName(0, out var name); // SIGDN_NORMALDISPLAY
            try { return Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        catch (COMException) { return null; }
        finally { Marshal.Release(pointer); }
    }
}
