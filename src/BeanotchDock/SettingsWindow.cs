using BeanotchDock.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace BeanotchDock;

// The dock's settings, in a window of their own (right-click the dock › Dock Settings…). Everything applies as it's
// changed: the dock moves, grows and restyles behind the window while you watch.
public sealed partial class SettingsWindow : Window
{
    private readonly DockSettings settings;
    private readonly DockWindow dock;
    private readonly StackPanel page = new() { Spacing = 8, Padding = new Thickness(24, 16, 24, 28), MaxWidth = 720 };
    private bool narrow;
    private TextBlock? updateStatus;

    public SettingsWindow(DockSettings settings, DockWindow dock)
    {
        this.settings = settings;
        this.dock = dock;
        Title = "Dock Settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "BeanotchDock.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 820));
        Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // Narrow windows put each control under its label instead of beside it.
        page.SizeChanged += (_, e) =>
        {
            var now = e.NewSize.Width < 560;
            if (now == narrow) return;
            narrow = now;
            Build();
        };
        Build();
    }

    private void Changed() => dock.SettingsChanged();

    private void Build()
    {
        page.Children.Clear();
        page.Children.Add(new TextBlock { Text = "Dock", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });

        Section("Appearance");
        Card(
            Row("Size", "How big the icons are at rest",
                Slide(24, 112, 2, settings.IconSize, v => settings.IconSize = v, v => $"{v:0} px")),
            Row("Magnification", "Icons grow as the pointer passes over them",
                Toggle(settings.Magnify, v => { settings.Magnify = v; Build(); })),
            settings.Magnify ? Row("Magnification amount", "How big a magnified icon gets",
                Slide(1.1, 3, 0.05, settings.Magnification, v => settings.Magnification = v, v => $"{v:0.00}×")) : null,
            Row("Position on screen", "",
                Choose(settings.Edge, v => settings.Edge = v)),
            Row("Look", "Auto follows Windows' light or dark mode",
                Choose(settings.Theme, v => settings.Theme = v)),
            Row("Glass", "How see-through the bar is",
                Slide(0.05, 1, 0.05, settings.Opacity, v => settings.Opacity = v, v => $"{v * 100:0}%")),
            Row("Corners", "From square to fully round",
                Slide(0, 40, 1, settings.Roundness, v => settings.Roundness = v, v => $"{v:0}")),
            Row("Spacing", "Room between icons",
                Slide(0, 0.5, 0.02, settings.Spacing, v => settings.Spacing = v, v => $"{v * 100:0}%")),
            Row("Distance from the edge", "Float the dock off the screen's edge, or sit it flush",
                Slide(0, 48, 1, settings.Margin, v => settings.Margin = v, v => $"{v:0} px")),
            Row("Show names", "A label beside the icon you point at",
                Toggle(settings.Labels, v => settings.Labels = v)),
            Row("Icon style", "Mac tiles set every app on a rounded square; right-click an app to give it your own picture",
                ChooseText(["Original", "Mac tiles"], settings.IconStyle == DockIconStyle.Tiles ? 1 : 0, i => settings.IconStyle = i == 1 ? DockIconStyle.Tiles : DockIconStyle.Original)),
            Row("Open apps", "The mark under apps that are open",
                Choose(settings.Indicator, v => settings.Indicator = v)));

        Section("Behaviour");
        Card(
            Row("Automatically hide and show the Dock", "It slides away and comes back when the pointer meets the edge",
                Toggle(settings.AutoHide, v => { settings.AutoHide = v; Build(); })),
            settings.AutoHide ? Row("Hide after", "How long it waits once the pointer leaves",
                Slide(0, 3, 0.1, settings.HideDelay, v => settings.HideDelay = v, v => $"{v:0.0} s")) : null,
            Row("Animate opening apps", "A bounce while an app starts",
                Toggle(settings.Bounce, v => settings.Bounce = v)),
            Row("Show open apps that aren't kept in the Dock", "They sit after the ones you keep",
                Toggle(settings.ShowRunning, v => settings.ShowRunning = v)),
            Row("Click the app in front to minimize it", "As on Windows. Off, as on the Mac: it stays",
                Toggle(settings.ClickMinimizes, v => settings.ClickMinimizes = v)),
            Row("Hide when an app is full screen", "Games, videos and slideshows keep the screen to themselves",
                Toggle(settings.HideInFullscreen, v => settings.HideInFullscreen = v)),
            Row("Show suggested and recent apps in Dock", "The last few apps you closed, after a divider",
                Toggle(settings.ShowRecent, v => settings.ShowRecent = v)),
            Row("Keyboard shortcut", "Ctrl+Alt+D turns hiding on and off, as ⌥⌘D does on the Mac", null));

        Section("Items");
        Card(
            Row("Start button", "Windows' Start, at the head of the dock", Toggle(settings.StartButton, v => settings.StartButton = v)),
            Row("Downloads", "Your Downloads folder, at the end", Toggle(settings.Downloads, v => settings.Downloads = v)),
            settings.Downloads ? Row("Display Downloads as", "A stack of the newest files, or the folder itself",
                ChooseText(["Stack", "Folder"], settings.DownloadsStack ? 0 : 1, i => settings.DownloadsStack = i == 0)) : null,
            Row("Recycle Bin", "At the very end; drop files on it to throw them away", Toggle(settings.Trash, v => settings.Trash = v)),
            Row("Divider", "The line before Downloads and the Recycle Bin", Toggle(settings.Separator, v => settings.Separator = v)));
        Pins();

        Section("System");
        Card(
            Row("Hide the Windows taskbar", "It slides up from the bottom edge when you need it, and comes back when the Dock quits",
                Toggle(settings.HideTaskbar, v => { settings.HideTaskbar = v; dock.TaskbarChanged(); }, apply: false)),
            Row("Open at sign-in", "", Toggle(Startup.On, Startup.Set, apply: false)));

        Section("Updates");
        var check = new Button { Content = "Check now" };
        check.Click += (_, _) => _ = dock.CheckForUpdateAsync(true);
        updateStatus = new TextBlock();
        Card(
            Row("Update automatically", "New versions install themselves; the Dock is back a few seconds later",
                Toggle(settings.AutoUpdate, v => settings.AutoUpdate = v, apply: false)),
            Row($"Beanotch Dock {dock.Updater.Current}", UpdateCaption(), check));
        UpdatesChanged();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var reset = new Button { Content = "Reset to defaults" };
        reset.Click += (_, _) => { settings.Reset(); Changed(); Build(); };
        buttons.Children.Add(reset);
        page.Children.Add(buttons);
    }

    private string UpdateCaption()
    {
        var u = dock.Updater;
        return u.Installing ? $"Installing {u.Latest}…"
            : u.Checking ? "Looking for a new version…"
            : u.Failed ? "Couldn't reach GitHub"
            : u.UpdateAvailable ? $"Version {u.Latest} is out"
            : u.CheckedAt is not null ? "Up to date"
            : "Looks for new versions every six hours";
    }

    // The version row's caption, kept current while the window is open.
    public void UpdatesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (page.Children.OfType<Border>().LastOrDefault() is { Child: StackPanel rows } && rows.Children.LastOrDefault() is Grid row
            && row.Children.OfType<StackPanel>().FirstOrDefault() is { } text && text.Children.Count > 1 && text.Children[1] is TextBlock caption)
            caption.Text = UpdateCaption();
    });

    // The kept apps, in order, to move up and down or take out.
    private void Pins()
    {
        var rows = new List<FrameworkElement?>();
        for (var i = 0; i < settings.Pins.Count; i++)
        {
            var index = i;
            var pin = settings.Pins[i];
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            Button Icon(string glyph, string tip, bool enabled, Action act)
            {
                var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6), IsEnabled = enabled };
                ToolTipService.SetToolTip(b, tip);
                b.Click += (_, _) => { act(); settings.Save(); Changed(); Build(); };
                return b;
            }
            actions.Children.Add(Icon("", "Move up", index > 0, () => (settings.Pins[index - 1], settings.Pins[index]) = (settings.Pins[index], settings.Pins[index - 1])));
            actions.Children.Add(Icon("", "Move down", index < settings.Pins.Count - 1, () => (settings.Pins[index + 1], settings.Pins[index]) = (settings.Pins[index], settings.Pins[index + 1])));
            actions.Children.Add(Icon("", "Remove from Dock", true, () => settings.Pins.RemoveAt(index)));
            rows.Add(Row(pin.Name, pin.Path.Length > 0 ? pin.Path : pin.Aumid ?? "", actions));
        }
        if (rows.Count == 0) rows.Add(Row("Nothing kept yet", "Right-click an open app in the Dock › Options › Keep in Dock", null));
        rows.Insert(0, Row("Tip", "Drag icons along the Dock to reorder them, or off it to take them out", null));
        Section("Kept in the Dock");
        Card([.. rows]);
    }

    private void Section(string title) =>
        page.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 18, 0, 4) });

    private void Card(params FrameworkElement?[] rows)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (row is null) continue;
            if (!first) stack.Children.Add(new Border { Height = 1, Background = Resource("CardStrokeColorDefaultBrush") });
            stack.Children.Add(row);
            first = false;
        }
        page.Children.Add(new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(8),
            Background = Resource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Resource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        });
    }

    private static Brush? Resource(string key) => Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private Grid Row(string title, string caption, FrameworkElement? control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (caption.Length > 0)
            text.Children.Add(new TextBlock { Text = caption, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var grid = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 16, RowSpacing = 8, MinHeight = 64 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!narrow) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            if (narrow)
            {
                // Under the label: switches stay at the start, sliders and lists take the width.
                control.HorizontalAlignment = control is ToggleSwitch ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(control, 1);
            }
            else Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return grid;
    }

    private ToggleSwitch Toggle(bool on, Action<bool> set, bool apply = true)
    {
        var t = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        t.Toggled += (_, _) =>
        {
            set(t.IsOn);
            if (apply) Changed(); else settings.Save();
        };
        return t;
    }

    // A slider with its value beside it.
    private Grid Slide(double min, double max, double step, double value, Action<double> set, Func<double, string> show)
    {
        var readout = new TextBlock { Text = show(value), MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(value, min, max), Width = narrow ? double.NaN : 200, MinWidth = 120 };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            readout.Text = show(e.NewValue);
            Changed();
        };
        // A grid rather than a stack, so on a narrow window the slider can take the room the readout leaves.
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(slider);
        Grid.SetColumn(readout, 1);
        row.Children.Add(readout);
        return row;
    }

    private ComboBox ChooseText(string[] options, int selected, Action<int> set)
    {
        var box = new ComboBox { MinWidth = narrow ? 0 : 160 };
        foreach (var o in options) box.Items.Add(o);
        box.SelectedIndex = selected;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { set(box.SelectedIndex); Changed(); } };
        return box;
    }

    // A drop-down of an enum's values.
    private ComboBox Choose<T>(T value, Action<T> set) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var box = new ComboBox { MinWidth = narrow ? 0 : 160 };
        foreach (var v in values) box.Items.Add(v.ToString());
        box.SelectedIndex = Array.IndexOf(values, value);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            set(values[box.SelectedIndex]);
            Changed();
        };
        return box;
    }
}
