using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BeanotchDock.Ui;

// A fully see-through window background, so only what the dock draws shows: the bar, the icons as they grow above it,
// their labels. WinUI has no such backdrop of its own; this hands the window a transparent system brush instead.
public sealed partial class ClearBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? compositor;
    private static nint queue;

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueOptions
    {
        public int Size, ThreadType, ApartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(QueueOptions options, out nint controller);

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        // The system compositor needs a Windows.System dispatcher queue on this thread; WinUI's own isn't one.
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && queue == 0)
            CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 }, out queue);
        compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }
}
