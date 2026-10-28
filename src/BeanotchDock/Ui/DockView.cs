using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace BeanotchDock.Ui;

// Draws the dock: a glass bar along the bottom with the icons on it, each growing as the pointer nears it (as on the
// Mac), a dot under the ones running, a label over the one pointed at, and a bounce while an app opens. Everything is
// placed by hand each frame; the window clips itself to what's drawn so the rest of the strip lets clicks through.
public sealed class DockView
{
    private readonly Canvas canvas = new();
    private readonly Border bar = new();
    private readonly Border label = new() { Padding = new Thickness(10, 4, 10, 5), CornerRadius = new CornerRadius(7), IsHitTestVisible = false };
    private readonly TextBlock labelText = new() { FontSize = 12.5, FontWeight = FontWeights.Medium };
    private readonly Dictionary<DockItem, (FrameworkElement Face, Ellipse Dot)> faces = [];
    private List<DockItem> items = [];
    private double width, height;
    private bool dark = true;

    public FrameworkElement View => canvas;
    public double IconSize { get; set; } = 52;
    public double Magnification { get; set; } = 1.65;
    // Where the pointer is along the dock, in the window's pixels; null when it's elsewhere.
    public double? PointerX { get; set; }
    public DockItem? Hovered { get; private set; }
    // 0 shown, 1 tucked below the screen's edge (auto-hide).
    public double Tucked { get; set; }

    public double Pad => Math.Round(IconSize * 0.14);
    public double Gap => Math.Round(IconSize * 0.1);
    public double BarHeight => IconSize + Pad * 2 + 4;
    // Room above the bar for magnified icons and the label.
    public double Height => BarHeight + IconSize * (Magnification - 1) + 46;

    public DockView()
    {
        label.Child = labelText;
        label.Visibility = Visibility.Collapsed;
        bar.CornerRadius = new CornerRadius(20);
        bar.BorderThickness = new Thickness(1);
        bar.IsHitTestVisible = false;
        canvas.Children.Add(bar);
        canvas.Children.Add(label);
        canvas.Background = new SolidColorBrush(Colors.Transparent);
        Restyle(true);
    }

    public void Restyle(bool darkMode)
    {
        dark = darkMode;
        bar.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = dark ? Color.FromArgb(150, 52, 52, 56) : Color.FromArgb(170, 250, 250, 252), Offset = 0 },
                new GradientStop { Color = dark ? Color.FromArgb(165, 30, 30, 34) : Color.FromArgb(185, 232, 232, 238), Offset = 1 },
            },
        };
        bar.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        label.Background = new SolidColorBrush(dark ? Color.FromArgb(235, 40, 40, 44) : Color.FromArgb(240, 246, 246, 248));
        label.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        label.BorderThickness = new Thickness(1);
        labelText.Foreground = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 20, 20, 20));
        foreach (var (_, dot) in faces.Values) dot.Fill = DotBrush();
    }

    private Brush DotBrush() => new SolidColorBrush(dark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(170, 0, 0, 0));

    public void Resize(double w, double h)
    {
        width = w;
        height = h;
        canvas.Width = w;
        canvas.Height = h;
    }

    // A new set of items: faces for the new ones, the gone ones taken out.
    public void SetItems(List<DockItem> next)
    {
        foreach (var gone in items.Where(i => !next.Contains(i)))
        {
            var (face, dot) = faces[gone];
            canvas.Children.Remove(face);
            canvas.Children.Remove(dot);
            faces.Remove(gone);
        }
        foreach (var item in next.Where(i => !faces.ContainsKey(i)))
        {
            FrameworkElement face = item.Kind == ItemKind.Separator
                ? new Rectangle { Width = 1, Fill = new SolidColorBrush(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0)) }
                : new Image { Stretch = Stretch.Uniform };
            face.IsHitTestVisible = false;
            var dot = new Ellipse { Width = 4, Height = 4, Fill = DotBrush(), IsHitTestVisible = false };
            canvas.Children.Insert(canvas.Children.IndexOf(label), face);
            canvas.Children.Insert(canvas.Children.IndexOf(label), dot);
            faces[item] = (face, dot);
        }
        items = next;
        foreach (var item in items) if (faces[item].Face is Image image) image.Source = item.Icon;
    }

    public void RefreshIcon(DockItem item)
    {
        if (faces.TryGetValue(item, out var f) && f.Face is Image image) image.Source = item.Icon;
    }

    // One frame: eases each icon's size toward where the pointer wants it, lays the dock out, and returns the parts
    // that take clicks (the bar and anything sticking out above it) for the window's shape.
    public List<Rect> Frame(DateTimeOffset now)
    {
        var s = IconSize;
        // The icons' resting centres, which the pointer's distance is measured from.
        var rest = new List<double>();
        double total = Pad * 2, x;
        foreach (var item in items) total += Width(item, 1);
        total += Gap * Math.Max(0, items.Count - 1);
        x = (width - total) / 2 + Pad;
        foreach (var item in items)
        {
            var w = Width(item, 1);
            rest.Add(x + w / 2);
            x += w + Gap;
        }

        var range = s * 3.2;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var target = 1.0;
            if (item.Kind != ItemKind.Separator && PointerX is double px && Magnification > 1)
            {
                var d = Math.Min(Math.Abs(px - rest[i]) / range, 1);
                target = 1 + (Magnification - 1) * (Math.Cos(d * Math.PI) + 1) / 2;
            }
            item.Scale += (target - item.Scale) * 0.32;
            if (Math.Abs(target - item.Scale) < 0.002) item.Scale = target;
        }

        total = Pad * 2 + Gap * Math.Max(0, items.Count - 1);
        foreach (var item in items) total += Width(item, item.Scale);
        var drop = Tucked * (BarHeight + 12);
        var barTop = height - 8 - BarHeight + drop;
        var left = (width - total) / 2;
        Place(bar, left, barTop, total, BarHeight);

        var shapes = new List<Rect> { new(left, barTop, total, BarHeight) };
        x = left + Pad;
        Hovered = null;
        foreach (var item in items)
        {
            var w = Width(item, item.Scale);
            var (face, dot) = faces[item];
            if (item.Kind == ItemKind.Separator)
            {
                Place(face, x + w / 2, barTop + Pad, 1, BarHeight - Pad * 2);
            }
            else
            {
                var bounce = 0.0;
                if (item.LaunchedAt is { } at)
                {
                    var t = (now - at).TotalSeconds;
                    if (t > 6 || item.Running) item.LaunchedAt = null;
                    else bounce = Math.Abs(Math.Sin(t * Math.PI * 1.6)) * s * 0.45;
                }
                var top = barTop + BarHeight - Pad - 4 - w - bounce;
                Place(face, x, top, w, w);
                if (top < barTop) shapes.Add(new Rect(x, top, w, barTop - top));
                Place(dot, x + w / 2 - 2, barTop + BarHeight - Pad + 1, 4, 4);
                dot.Opacity = item.Running ? 1 : 0;
                if (PointerX is double px && px >= x - Gap / 2 && px < x + w + Gap / 2) Hovered = item;
            }
            item.X = x;
            x += w + Gap;
        }

        if (Hovered is { } hovered && Tucked < 0.5)
        {
            labelText.Text = hovered.Label;
            label.Visibility = Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = label.DesiredSize;
            var w = Width(hovered, hovered.Scale);
            var top = barTop + BarHeight - Pad - 4 - w - size.Height - 10;
            var lx = Math.Clamp(hovered.X + w / 2 - size.Width / 2, 4, width - size.Width - 4);
            Canvas.SetLeft(label, lx);
            Canvas.SetTop(label, top);
            shapes.Add(new Rect(lx, top, size.Width, size.Height));
        }
        else
        {
            label.Visibility = Visibility.Collapsed;
        }
        return shapes;
    }

    // Whether anything is still moving, so the window can stop drawing frames when the dock is at rest.
    public bool Settled => items.All(i => i.LaunchedAt is null && (i.Scale == 1 || PointerX is not null) && Math.Abs(i.Scale - Target(i)) < 0.002);

    private double Target(DockItem item) => PointerX is null || item.Kind == ItemKind.Separator ? 1 : item.Scale;

    public DockItem? ItemAt(double px) =>
        items.FirstOrDefault(i => i.Kind != ItemKind.Separator && px >= i.X - Gap / 2 && px < i.X + Width(i, i.Scale) + Gap / 2);

    private double Width(DockItem item, double scale) => item.Kind == ItemKind.Separator ? Gap : IconSize * scale;

    private static void Place(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w);
        el.Height = Math.Max(0, h);
    }
}
