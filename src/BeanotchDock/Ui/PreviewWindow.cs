using System.Runtime.InteropServices;
using BeanotchDock.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace BeanotchDock.Ui;

// Live pictures of an app's windows above its icon, after the pointer rests on it: Windows draws each one into this
// window (a DWM thumbnail), so they move as the windows do and cost the dock nothing to paint. A click brings that
// window forward.
public sealed partial class PreviewWindow : Window
{
    private const double Tile = 216, Shot = 132, TitleHeight = 22, Pad = 10;
    private readonly nint hwnd;
    private readonly List<nint> thumbs = [];
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Border panel = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(Pad), BorderThickness = new Thickness(1) };
    public IReadOnlyList<nint> Showing { get; private set; } = [];
    public RectInt32 Bounds { get; private set; }
    public Action<nint>? Picked;

    public PreviewWindow()
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        panel.Child = row;
        Content = new Grid { Background = new SolidColorBrush(Colors.Transparent), Children = { panel } };
        SystemBackdrop = new DesktopAcrylicBackdrop();
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Native.Frameless(hwnd);
    }

    // Shows the windows, centred over `centreX` with the bottom at `bottom` (screen pixels), at the dock's scale.
    public void Show(IReadOnlyList<nint> windows, int centreX, int bottom, double scale, bool dark)
    {
        Clear();
        Showing = [.. windows.Take(5)];
        panel.Background = new SolidColorBrush(dark ? Color.FromArgb(150, 28, 28, 32) : Color.FromArgb(170, 246, 246, 248));
        panel.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        row.Children.Clear();
        foreach (var w in Showing)
        {
            var title = new TextBlock
            {
                Text = Native.Title(w) is { Length: > 0 } t ? t : "Window", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 20, 20, 20)), Margin = new Thickness(2, 0, 2, 0),
            };
            // The picture itself is drawn by Windows over this space; the button under it takes the click.
            var tile = new Grid { Width = Tile, Height = Shot + TitleHeight };
            tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Shot) });
            tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
            Grid.SetRow(title, 1);
            tile.Children.Add(title);
            var button = new Button
            {
                Content = tile, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.Transparent),
            };
            var window = w;
            button.Click += (_, _) => Picked?.Invoke(window);
            row.Children.Add(button);
        }
        int width = (int)Math.Round((Pad * 2 + Showing.Count * Tile + (Showing.Count - 1) * 8 + 2) * scale);
        int height = (int)Math.Round((Pad * 2 + Shot + TitleHeight + 2) * scale);
        Bounds = new RectInt32(centreX - width / 2, bottom - height, width, height);
        AppWindow.MoveAndResize(Bounds);
        AppWindow.Show(false);
        for (var i = 0; i < Showing.Count; i++)
        {
            if (DwmRegisterThumbnail(hwnd, Showing[i], out var thumb) != 0) continue;
            thumbs.Add(thumb);
            // Fitted inside its tile, keeping the window's shape.
            DwmQueryThumbnailSourceSize(thumb, out var source);
            double boxW = Tile * scale, boxH = (Shot - 6) * scale;
            var fit = source.X > 0 && source.Y > 0 ? Math.Min(boxW / source.X, boxH / source.Y) : 1;
            double w = source.X * fit, h = source.Y * fit;
            double left = (Pad + 1 + i * (Tile + 8)) * scale + (boxW - w) / 2, top = (Pad + 1) * scale + (boxH - h) / 2;
            var props = new ThumbnailProperties
            {
                Flags = 0x1 | 0x8, // DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE
                Destination = new ThumbRect { Left = (int)left, Top = (int)top, Right = (int)(left + w), Bottom = (int)(top + h) },
                Visible = 1,
            };
            DwmUpdateThumbnailProperties(thumb, props);
        }
    }

    public void Hide()
    {
        Clear();
        Showing = [];
        AppWindow.Hide();
    }

    private void Clear()
    {
        foreach (var thumb in thumbs) DwmUnregisterThumbnail(thumb);
        thumbs.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public ThumbRect Destination;
        public ThumbRect Source;
        public byte Opacity;
        public int Visible;
        public int SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceSize { public int X, Y; }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmUnregisterThumbnail(nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmUpdateThumbnailProperties(nint thumbnail, in ThumbnailProperties properties);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmQueryThumbnailSourceSize(nint thumbnail, out SourceSize size);
}
