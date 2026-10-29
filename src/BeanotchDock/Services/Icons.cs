using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace BeanotchDock.Services;

// Large icons (256 px, so magnified ones stay sharp) from the shell, the way Explorer draws them: for a program or a
// shortcut, a Store app by its ID, the Recycle Bin (full or empty) and the Downloads folder. Or a picture of your
// own, and either one set on a Mac-style tile.
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

    // Top-down BGRA with premultiplied alpha, which is what a WriteableBitmap holds.
    private readonly record struct Pixels(byte[] Data, int Width, int Height);

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
        ItemKind.Folder => item.Folder,
        ItemKind.NowPlaying => null,
        _ => item.Pin is { Path.Length: > 0 } pin && (File.Exists(pin.Path) || Directory.Exists(pin.Path)) ? pin.Path
            : item.Source?.Path is { } path && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && item.Source.Aumid is null ? path
            : (item.Pin?.Aumid ?? item.Source?.Aumid) is { } aumid ? "shell:AppsFolder\\" + aumid
            : item.Source?.Path,
    };

    // Any file's or folder's icon, as Explorer draws it.
    public static ImageSource? ForFile(string path) => Load(path);

    // An item's icon: a picture you chose for it, else the shell's. Apps (and Start) go on a tile when tiles are on;
    // Downloads and the Recycle Bin keep their own shapes, as on the Mac.
    public static async Task<ImageSource?> ForAsync(DockItem item, string? custom = null, bool tile = false, bool dark = false)
    {
        tile &= item.Kind is ItemKind.App or ItemKind.Start;
        var source = custom is not null ? "file:" + custom : item.Kind == ItemKind.Start ? "start" : Source(item);
        if (source is null) return null;
        var key = tile ? source + (dark ? "|tile-dark" : "|tile") : source;
        // The Recycle Bin's picture changes with what's in it, so it's never kept.
        if (item.Kind != ItemKind.Trash && Cache.TryGetValue(key, out var known)) return known;
        var pixels = custom is not null ? await DecodeAsync(custom) : item.Kind == ItemKind.Start ? StartLogo() : Pixel(source);
        // A chosen picture that's gone or unreadable: the usual icon instead.
        if (pixels is null && custom is not null) return await ForAsync(item, null, tile, dark);
        if (pixels is not { } p) return null;
        var image = ToImage(tile ? Tile(p, dark) : p);
        if (item.Kind != ItemKind.Trash) Cache[key] = image;
        return image;
    }

    // A picture file (PNG, ICO, JPEG and the rest Windows can read), its largest size, at most 256 px across.
    private static async Task<Pixels?> DecodeAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var frame = await decoder.GetFrameAsync(0);
            for (uint i = 1; i < decoder.FrameCount; i++)
                if (await decoder.GetFrameAsync(i) is { } f && f.PixelWidth > frame.PixelWidth) frame = f;
            var fit = Math.Min(1, 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
            uint width = Math.Max(1, (uint)Math.Round(frame.PixelWidth * fit)), height = Math.Max(1, (uint)Math.Round(frame.PixelHeight * fit));
            var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            return new Pixels(data.DetachPixelData(), (int)width, (int)height);
        }
        catch (Exception e) { App.Log(e); return null; }
    }

    private static ImageSource? Load(string source) => Pixel(source) is { } p ? ToImage(p) : null;

    private static Pixels? Pixel(string source)
    {
        if (SHCreateItemFromParsingName(source, 0, ImageFactoryId, out var pointer) != 0 || pointer == 0) return null;
        nint hbitmap = 0;
        try
        {
            var factory = (IShellItemImageFactory)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            factory.GetImage(new Size(256, 256), 0x1 | 0x4, out hbitmap); // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
            return hbitmap == 0 ? null : Read(hbitmap);
        }
        catch (COMException) { return null; }
        finally
        {
            if (hbitmap != 0) Native.DeleteObject(hbitmap);
            Marshal.Release(pointer);
        }
    }

    // The shell's bitmap, already premultiplied.
    private static Pixels? Read(nint hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<Bitmap>(), out var bmp) == 0 || bmp.Width <= 0 || bmp.Height <= 0) return null;
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = bmp.Width, Height = -bmp.Height, Planes = 1, BitCount = 32 };
        var pixels = new byte[bmp.Width * bmp.Height * 4];
        var dc = GetDC(0);
        try { if (GetDIBits(dc, hbitmap, 0, (uint)bmp.Height, pixels, ref header, 0) == 0) return null; }
        finally { ReleaseDC(0, dc); }
        return new Pixels(pixels, bmp.Width, bmp.Height);
    }

    private static WriteableBitmap ToImage(Pixels p)
    {
        var image = new WriteableBitmap(p.Width, p.Height);
        using (var stream = image.PixelBuffer.AsStream()) stream.Write(p.Data, 0, p.Data.Length);
        image.Invalidate();
        return image;
    }

    // Standing in for a song without a cover: a music note in Windows' own icon font, on a soft tile.
    private static ImageSource? note;
    public static ImageSource Note => note ??= ToImage(NotePixels());

    private static Pixels NotePixels()
    {
        const int size = 256;
        var px = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                double dx = Math.Max(Math.Abs(x - 128) - 84, 0), dy = Math.Max(Math.Abs(y - 128) - 84, 0);
                var inside = Math.Sqrt(dx * dx + dy * dy) < 28;
                if (!inside) continue;
                // A pink-to-orange tile with a white quaver: a stem, a flag and a round head.
                var t = (x + y) / 512.0;
                byte r = (byte)(255 - 20 * t), g = (byte)(90 + 60 * t), b = (byte)(120 - 70 * t);
                var note = (x >= 140 && x < 152 && y >= 70 && y < 170) || (x >= 140 && x < 186 && y >= 70 && y < 84 + (x - 140) / 4)
                           || Math.Pow(x - 128, 2) / 1.2 + Math.Pow(y - 172, 2) < 22 * 22;
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = note ? ((byte)255, (byte)255, (byte)255, (byte)255) : (b, g, r, (byte)255);
            }
        return new Pixels(px, size, size);
    }

    // Windows' four panes in its blue, drawn rather than borrowed: the shell has no picture of the Start button.
    private static Pixels StartLogo()
    {
        const int size = 256, gap = 10, pad = 36;
        var pixels = new byte[size * size * 4];
        var pane = (size - pad * 2 - gap) / 2;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                int px = x - pad, py = y - pad;
                if (px < 0 || py < 0 || px >= pane * 2 + gap || py >= pane * 2 + gap) continue;
                if ((px >= pane && px < pane + gap) || (py >= pane && py < pane + gap)) continue;
                // Light at the top left to deep at the bottom right, as in Windows 11's logo.
                var t = (px + py) / (double)((pane * 2 + gap) * 2);
                var i = (y * size + x) * 4;
                pixels[i] = (byte)(255 - 40 * t);
                pixels[i + 1] = (byte)(170 - 90 * t);
                pixels[i + 2] = (byte)(40 - 30 * t);
                pixels[i + 3] = 255;
            }
        return new Pixels(pixels, size, size);
    }

    // A Mac-style tile: a rounded square (a superellipse, the Mac's shape) in a pale tint of the icon's own colour,
    // or a deep one in the dark look, lit from the top, with a soft shadow under it and the icon set in the middle.
    // An icon that already fills a square, like a Store app's, is clipped to the shape instead of set on one.
    private static Pixels Tile(Pixels icon, bool dark)
    {
        const int size = 256;
        const double half = 104, cx = 128, cy = 124, n = 5;
        var src = icon.Data;
        int w = icon.Width, h = icon.Height, minX = w, minY = h, maxX = -1, maxY = -1, solid = 0;
        double r = 0, g = 0, b = 0, weight = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                int a = src[i + 3];
                if (a < 16) continue;
                (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                // Colourful pixels count for more than grey ones, so a tile takes the icon's colour, not its outline.
                double bl = src[i], gr = src[i + 1], rd = src[i + 2];
                var chroma = (Math.Max(rd, Math.Max(gr, bl)) - Math.Min(rd, Math.Min(gr, bl))) / Math.Max(1, a);
                var k = 0.1 + chroma;
                (b, g, r, weight) = (b + bl * k, g + gr * k, r + rd * k, weight + a * k);
            }
        if (maxX < 0) return icon;
        int boxW = maxX - minX + 1, boxH = maxY - minY + 1;
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
                if (src[(y * w + x) * 4 + 3] > 230) solid++;
        var full = Math.Abs(boxW - boxH) <= Math.Max(boxW, boxH) * 0.08 && solid > boxW * boxH * 0.86;
        (r, g, b) = weight > 0 ? (r / weight, g / weight, b / weight) : (0.5, 0.5, 0.5);

        // Where the icon goes: across the whole tile, or in its middle with room around it.
        var inner = full ? half * 2 : half * 2 * 0.7;
        var fit = inner / Math.Max(boxW, boxH);
        double left = cx - boxW * fit / 2, top = cy - boxH * fit / 2;

        var output = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                var d = Distance((px - cx) / half, (py - cy) / half, n);
                var cover = Math.Clamp((1 - d) * half + 0.5, 0, 1);
                // The shadow: the same shape a little lower, fading out over a few pixels.
                var ds = Distance((px - cx) / half, (py - cy - 5) / half, n);
                var shadow = 0.3 * Math.Clamp(1 - (ds - 0.97) * half / 12, 0, 1);

                double ob = 0, og = 0, or = 0, oa = 0;
                if (cover > 0)
                {
                    // Lighter at the top, a touch deeper at the bottom: near white in the light look, near black with a
                    // hint of the icon's colour in the dark one.
                    var t = (py - (cy - half)) / (half * 2);
                    double tb, tg, tr;
                    if (dark)
                    {
                        var lift = 0.2 - 0.07 * t;
                        (tb, tg, tr) = ((b * 0.16 + lift) * 255, (g * 0.16 + lift) * 255, (r * 0.16 + lift) * 255);
                    }
                    else
                    {
                        var mix = 0.9 - 0.08 * t;
                        (tb, tg, tr) = ((b * (1 - mix) + mix) * 255, (g * (1 - mix) + mix) * 255, (r * (1 - mix) + mix) * 255);
                    }
                    // A faint rim just inside the edge: darker on a light tile, lighter on a dark one.
                    if ((1 - d) * half < 1.5)
                        (tb, tg, tr) = dark ? (tb + 22, tg + 22, tr + 22) : (tb * 0.9, tg * 0.9, tr * 0.9);
                    var (sb, sg, sr, sa) = Sample(icon, minX + (px - left) / fit - 0.5, minY + (py - top) / fit - 0.5);
                    (ob, og, or) = (sb + tb * (1 - sa / 255), sg + tg * (1 - sa / 255), sr + tr * (1 - sa / 255));
                    (ob, og, or, oa) = (ob * cover, og * cover, or * cover, 255 * cover);
                }
                // The shadow under what the tile doesn't cover.
                oa += 255 * shadow * (1 - cover);
                var o = (y * size + x) * 4;
                (output[o], output[o + 1], output[o + 2], output[o + 3]) =
                    ((byte)Math.Clamp(ob, 0, 255), (byte)Math.Clamp(og, 0, 255), (byte)Math.Clamp(or, 0, 255), (byte)Math.Clamp(oa, 0, 255));
            }
        return new Pixels(output, size, size);
    }

    // How far out a point is on a superellipse of radius 1: below 1 inside, 1 on the edge.
    private static double Distance(double x, double y, double n) => Math.Pow(Math.Pow(Math.Abs(x), n) + Math.Pow(Math.Abs(y), n), 1 / n);

    // The icon between its pixels, blended from the four around; clear outside it.
    private static (double B, double G, double R, double A) Sample(Pixels p, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0, b = 0, g = 0, r = 0, a = 0;
        for (var j = 0; j < 2; j++)
            for (var i = 0; i < 2; i++)
            {
                int sx = x0 + i, sy = y0 + j;
                if (sx < 0 || sy < 0 || sx >= p.Width || sy >= p.Height) continue;
                var k = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                var o = (sy * p.Width + sx) * 4;
                (b, g, r, a) = (b + p.Data[o] * k, g + p.Data[o + 1] * k, r + p.Data[o + 2] * k, a + p.Data[o + 3] * k);
            }
        return (b, g, r, a);
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
