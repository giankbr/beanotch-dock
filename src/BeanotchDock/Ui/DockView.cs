using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace BeanotchDock.Ui;

// Draws the dock: a glass bar along an edge of the screen with the icons on it, each growing as the pointer nears it
// (as on the Mac), a mark under the ones running, a label beside the one pointed at, and a bounce while an app opens.
// Everything is placed by hand each frame, along the dock ("main" axis) and out from the screen's edge ("cross" axis),
// so the same layout serves the bottom, left and right edges. The window clips itself to what's drawn so the rest of
// the strip lets clicks through.
public sealed class DockView
{
    private readonly Canvas canvas = new();
    private readonly Border bar = new();
    private readonly Border label = new() { Padding = new Thickness(10, 4, 10, 5), CornerRadius = new CornerRadius(7), IsHitTestVisible = false };
    private readonly TextBlock labelText = new() { FontSize = 12.5, FontWeight = FontWeights.Medium };
    private readonly Dictionary<DockItem, (FrameworkElement Face, Rectangle Mark)> faces = [];
    private List<DockItem> items = [];
    private double width, height;
    private bool dark = true;

    public FrameworkElement View => canvas;
    public double IconSize { get; set; } = 52;
    public double Magnification { get; set; } = 1.7;
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    // The bar's glass: how solid (0–1), how round, the gap between icons (a share of an icon) and from the screen's edge.
    public double Opacity { get; set; } = 0.8;
    public double Radius { get; set; } = 20;
    public double Spacing { get; set; } = 0.1;
    public double Margin { get; set; } = 8;
    public bool Labels { get; set; } = true;
    public DockIndicator Indicator { get; set; } = DockIndicator.Dot;
    public bool Bounce { get; set; } = true;
    // Where the pointer is along the dock, in the window's pixels; null when it's elsewhere.
    public double? Pointer { get; set; }
    public DockItem? Hovered { get; private set; }
    // 0 shown, 1 tucked past the screen's edge (auto-hide).
    public double Tucked { get; set; }

    private bool Side => Edge != DockEdge.Bottom;
    // The icon size in use: the chosen one, or smaller when that many icons wouldn't fit along the screen.
    private double fit = double.MaxValue;
    private double S => Math.Min(IconSize, fit);
    public double Pad => Math.Round(S * 0.14);
    public double Gap => Math.Round(S * Spacing);
    public double BarThickness => S + Pad * 2 + 4;
    // How deep the window has to be: the bar, magnified icons above it, and the label beyond them.
    public double Thickness => Margin + BarThickness + IconSize * (Magnification - 1) + (Side ? 230 : 46);

    public DockView()
    {
        label.Child = labelText;
        label.Visibility = Visibility.Collapsed;
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
        var alpha = Math.Clamp(Opacity, 0.05, 1);
        bar.CornerRadius = new CornerRadius(Radius);
        bar.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = Side ? new Point(1, 0) : new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = dark ? Color.FromArgb((byte)(230 * alpha), 52, 52, 56) : Color.FromArgb((byte)(255 * alpha), 250, 250, 252), Offset = 0 },
                new GradientStop { Color = dark ? Color.FromArgb((byte)(250 * alpha), 30, 30, 34) : Color.FromArgb((byte)(255 * alpha), 232, 232, 238), Offset = 1 },
            },
        };
        bar.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        label.Background = new SolidColorBrush(dark ? Color.FromArgb(235, 40, 40, 44) : Color.FromArgb(240, 246, 246, 248));
        label.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        label.BorderThickness = new Thickness(1);
        labelText.Foreground = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 20, 20, 20));
        foreach (var (face, mark) in faces.Values)
        {
            mark.Fill = MarkBrush();
            if (face is Rectangle line) line.Fill = LineBrush();
        }
    }

    private Brush MarkBrush() => new SolidColorBrush(dark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(170, 0, 0, 0));
    private Brush LineBrush() => new SolidColorBrush(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0));

    public void Resize(double w, double h)
    {
        width = w;
        height = h;
        canvas.Width = w;
        canvas.Height = h;
        Fit();
    }

    // Every icon, its share of padding and spacing, and the dividers, within the screen's length less a margin.
    private void Fit()
    {
        var icons = items.Count(i => i.Kind != ItemKind.Separator);
        var dividers = items.Count - icons;
        if (icons == 0 || Length <= 0) { fit = double.MaxValue; return; }
        fit = Math.Max(16, (Length - 24 - dividers * 6) / (0.28 + icons + Spacing * Math.Max(0, items.Count - 1)));
    }

    // A new set of items: faces for the new ones, the gone ones taken out.
    public void SetItems(List<DockItem> next)
    {
        foreach (var gone in items.Where(i => !next.Contains(i)))
        {
            var (face, mark) = faces[gone];
            canvas.Children.Remove(face);
            canvas.Children.Remove(mark);
            faces.Remove(gone);
        }
        foreach (var item in next.Where(i => !faces.ContainsKey(i)))
        {
            FrameworkElement face = item.Kind == ItemKind.Separator ? new Rectangle { Fill = LineBrush() } : new Image { Stretch = Stretch.Uniform };
            face.IsHitTestVisible = false;
            var mark = new Rectangle { Fill = MarkBrush(), IsHitTestVisible = false, Opacity = 0 };
            canvas.Children.Insert(canvas.Children.IndexOf(label), face);
            canvas.Children.Insert(canvas.Children.IndexOf(label), mark);
            faces[item] = (face, mark);
        }
        items = next;
        Fit();
        foreach (var item in items) if (faces[item].Face is Image image) image.Source = item.Icon;
    }

    public void RefreshIcon(DockItem item)
    {
        if (faces.TryGetValue(item, out var f) && f.Face is Image image) image.Source = item.Icon;
    }

    // A rectangle given along the dock (m, length lm) and out from the screen's edge (c, depth lc), in canvas terms.
    private Rect At(double m, double c, double lm, double lc) => Edge switch
    {
        DockEdge.Left => new Rect(c, m, lc, lm),
        DockEdge.Right => new Rect(width - c - lc, m, lc, lm),
        _ => new Rect(m, height - c - lc, lm, lc),
    };

    private double Length => Side ? height : width;

    // One frame: eases each icon's size toward where the pointer wants it, lays the dock out, and returns the parts
    // that take clicks (the bar first, then anything sticking out beyond it) for the window's shape.
    public List<Rect> Frame(DateTimeOffset now)
    {
        var s = S;
        // The icons' resting centres, which the pointer's distance is measured from.
        var rest = new List<double>();
        double total = Pad * 2 + Gap * Math.Max(0, items.Count - 1), m;
        foreach (var item in items) total += Size(item, 1);
        m = (Length - total) / 2 + Pad;
        foreach (var item in items)
        {
            var w = Size(item, 1);
            rest.Add(m + w / 2);
            m += w + Gap;
        }

        var range = s * 3.2;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var target = 1.0;
            if (item.Kind != ItemKind.Separator && Pointer is double p && Magnification > 1)
            {
                var d = Math.Min(Math.Abs(p - rest[i]) / range, 1);
                target = 1 + (Magnification - 1) * (Math.Cos(d * Math.PI) + 1) / 2;
            }
            item.Scale += (target - item.Scale) * 0.32;
            if (Math.Abs(target - item.Scale) < 0.002) item.Scale = target;
        }

        total = Pad * 2 + Gap * Math.Max(0, items.Count - 1);
        foreach (var item in items) total += Size(item, item.Scale);
        var barC = Margin - Tucked * (Margin + BarThickness + 12);
        var start = (Length - total) / 2;
        var barRect = At(start, barC, total, BarThickness);
        Put(bar, barRect);

        var shapes = new List<Rect> { barRect };
        m = start + Pad;
        Hovered = null;
        foreach (var item in items)
        {
            var w = Size(item, item.Scale);
            var (face, mark) = faces[item];
            if (item.Kind == ItemKind.Separator)
            {
                Put(face, At(m + w / 2, barC + Pad, 1, BarThickness - Pad * 2));
                mark.Opacity = 0;
            }
            else
            {
                var bounce = 0.0;
                if (item.LaunchedAt is { } at)
                {
                    var t = (now - at).TotalSeconds;
                    if (t > 6 || item.Running) item.LaunchedAt = null;
                    else if (Bounce) bounce = Math.Abs(Math.Sin(t * Math.PI * 1.6)) * s * 0.45;
                }
                var iconC = barC + Pad + 4 + bounce;
                Put(face, At(m, iconC, w, w));
                if (iconC + w > barC + BarThickness) shapes.Add(At(m, barC + BarThickness, w, iconC + w - barC - BarThickness));
                // The running mark, between the icon and the screen's edge: a dot or a short line.
                var markC = barC + (Pad + 4) / 2 - 1;
                if (Indicator == DockIndicator.Line)
                {
                    Put(mark, At(m + w * 0.3, markC, w * 0.4, 3));
                    mark.RadiusX = mark.RadiusY = 1.5;
                }
                else
                {
                    Put(mark, At(m + w / 2 - 2, markC, 4, 4));
                    mark.RadiusX = mark.RadiusY = 2;
                }
                mark.Opacity = item.Running && Indicator != DockIndicator.None ? 1 : 0;
                if (Pointer is double p && p >= m - Gap / 2 && p < m + w + Gap / 2) Hovered = item;
            }
            item.X = m;
            m += w + Gap;
        }

        if (Labels && Hovered is { } hovered && Tucked < 0.5)
        {
            labelText.Text = hovered.Label;
            label.Visibility = Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = label.DesiredSize;
            var w = Size(hovered, hovered.Scale);
            var beyond = barC + Pad + 4 + w + 10;
            // Along the dock, the label is as long as it is wide (bottom) or tall (sides).
            var along = Side ? size.Height : size.Width;
            var lm = Math.Clamp(hovered.X + w / 2 - along / 2, 4, Math.Max(4, Length - along - 4));
            var rect = At(lm, beyond, along, Side ? size.Width : size.Height);
            Canvas.SetLeft(label, rect.X);
            Canvas.SetTop(label, rect.Y);
            shapes.Add(rect);
        }
        else
        {
            label.Visibility = Visibility.Collapsed;
        }
        return shapes;
    }

    // Whether anything is still moving, so the window can stop drawing frames when the dock is at rest.
    public bool Settled => items.All(i => i.LaunchedAt is null && (Pointer is not null || i.Scale == 1));

    public DockItem? ItemAt(double p) =>
        items.FirstOrDefault(i => i.Kind != ItemKind.Separator && p >= i.X - Gap / 2 && p < i.X + Size(i, i.Scale) + Gap / 2);

    private double Size(DockItem item, double scale) => item.Kind == ItemKind.Separator ? Math.Max(Gap, 6) : S * scale;

    private static void Put(FrameworkElement el, Rect r)
    {
        Canvas.SetLeft(el, r.X);
        Canvas.SetTop(el, r.Y);
        el.Width = Math.Max(0, r.Width);
        el.Height = Math.Max(0, r.Height);
    }
}
