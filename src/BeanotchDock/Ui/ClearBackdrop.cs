using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BeanotchDock.Ui;

// A fully see-through window background, so only what the dock draws shows: the bar, the icons as they grow beyond it,
// their labels. WinUI has no such backdrop of its own. A transparent system brush isn't enough on its own: DWM also
// has to be told to blend the window (an empty blur-behind region, the frame extended into the client area), and the
// window's own background has to be painted black, which DWM then treats as clear. The approach follows WinUIEx's
// TransparentTintBackdrop (MIT).
public sealed partial class ClearBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? compositor;
    private static nint queue;
    private nint hwnd;

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueOptions { public int Size, ThreadType, ApartmentType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int TransitionOnMaximized; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(QueueOptions options, out nint controller);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, in Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmEnableBlurBehindWindow(nint hwnd, in BlurBehind blur);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, in Rect rect, nint brush);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    private static readonly nint Black = CreateSolidBrush(0);

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        // The system compositor needs a Windows.System dispatcher queue on this thread; WinUI's own isn't one.
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && queue == 0)
            CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 }, out queue);
        compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        hwnd = (nint)root.ContentIslandEnvironment.AppWindowId.Value;
        Configure(hwnd);
        unsafe { SetWindowSubclass(hwnd, &Subclass, 1, 0); }
        var dc = GetDC(hwnd);
        Clear(hwnd, dc);
        ReleaseDC(hwnd, dc);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }

    private static void Configure(nint hwnd)
    {
        DwmExtendFrameIntoClientArea(hwnd, new Margins());
        // DWM_BB_ENABLE | DWM_BB_BLURREGION, with a region off the window: blending on, no actual blur.
        DwmEnableBlurBehindWindow(hwnd, new BlurBehind { Flags = 3, Enable = 1, Region = CreateRectRgn(-2, -2, -1, -1) });
    }

    private static bool Clear(nint hwnd, nint dc)
    {
        if (!GetClientRect(hwnd, out var rect)) return false;
        FillRect(dc, rect, Black);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Subclass(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        switch (message)
        {
            case 0x0014 when Clear(hwnd, wParam): // WM_ERASEBKGND
                return 1;
            case 0x031E: // WM_DWMCOMPOSITIONCHANGED
                Configure(hwnd);
                return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
}
