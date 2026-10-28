using BeanotchDock.Services;
using BeanotchDock.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI.ViewManagement;

namespace BeanotchDock;

// The dock's window: a borderless, always-on-top strip along an edge of the main screen, see-through except for what
// the dock draws, and clipped to it so clicks elsewhere reach the windows behind. It keeps the item list in step with
// the pins and the running apps, draws frames while anything moves, and hands clicks, drops and menus to the shell.
public sealed partial class DockWindow : Window
{
    public static string? SnapshotFolder => Environment.GetEnvironmentVariable("BEANOTCH_DOCK_SNAPSHOT") is { Length: > 0 } dir ? dir : null;

    private readonly DockSettings settings = DockSettings.Load();
    private readonly DockView view = new();
    private readonly Grid root = new();
    private readonly nint hwnd;
    private readonly DispatcherQueueTimer poll, hideCheck, updateTimer;
    public Updater Updater { get; } = new();
    private readonly Tasks tasks;
    private List<DockItem> items = [];
    private readonly DockItem separator = new() { Kind = ItemKind.Separator, Key = "|" };
    private readonly DockItem downloads = new() { Kind = ItemKind.Downloads, Key = "downloads", Label = "Downloads" };
    private readonly DockItem trash = new() { Kind = ItemKind.Trash, Key = "trash", Label = "Recycle Bin" };
    private readonly DockItem start = new() { Kind = ItemKind.Start, Key = "start", Label = "Start" };
    private bool hiddenForFullscreen;
    private bool animating;
    private double scale = 1;
    private RectInt32 screen;
    private string lastShape = "";
    private DateTimeOffset pointerAway = DateTimeOffset.MinValue;

    public DockWindow()
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ui = DispatcherQueue.GetForCurrentThread();
        tasks = new Tasks(hwnd);

        root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        root.Children.Add(view.View);
        root.PointerMoved += (_, e) => Point(e);
        root.PointerEntered += (_, e) => Point(e);
        root.PointerExited += (_, _) => { view.Pointer = null; pointerAway = DateTimeOffset.Now; Animate(); };
        root.PointerReleased += OnClick;
        root.RightTapped += OnRightTapped;
        root.AllowDrop = true;
        root.DragOver += OnDragOver;
        root.Drop += OnDrop;
        Content = root;
        SystemBackdrop = new ClearBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        Native.Frameless(hwnd);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Beanotch Dock";
        AppWindow.Closing += (_, _) => Quit();

        new UISettings().ColorValuesChanged += (_, _) => ui.TryEnqueue(() => view.Restyle(Dark));
        Apply();

        if (!settings.Imported) ImportTaskbarPins();
        if (settings.HideTaskbar) Taskbar.Hide(settings);
        Refresh();

        // The running apps, checked twice a second: cheap, and it also catches title and icon changes.
        poll = ui.CreateTimer();
        poll.Interval = TimeSpan.FromMilliseconds(500);
        poll.Tick += (_, _) => { Refresh(); WatchFullscreen(); };
        poll.Start();
        // With auto-hide on, the pointer at the screen's bottom edge brings the dock up.
        hideCheck = ui.CreateTimer();
        hideCheck.Interval = TimeSpan.FromMilliseconds(80);
        hideCheck.Tick += (_, _) => WatchEdge();
        hideCheck.Start();

        // New versions are looked for a minute after start and every six hours.
        updateTimer = ui.CreateTimer();
        updateTimer.Interval = TimeSpan.FromHours(6);
        updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync(false);
        updateTimer.Start();
        if (SnapshotFolder is null) Once(ui.CreateTimer(), TimeSpan.FromMinutes(1), () => _ = CheckForUpdateAsync(false));

        CompositionTarget.Rendering += (_, _) => { if (animating) Draw(); };
        Animate();
        if (SnapshotFolder is { } dir) _ = SnapshotAsync(dir);
    }

    private static bool SystemDark() => new UISettings().GetColorValue(UIColorType.Background).R < 128;

    private bool Dark => settings.Theme switch { DockTheme.Light => false, DockTheme.Dark => true, _ => SystemDark() };

    // The settings applied, and the strip sized and placed along its edge of the main screen: as long as the screen,
    // and deep enough for magnified icons and labels.
    public void Apply()
    {
        view.IconSize = Math.Clamp(settings.IconSize, 24, 128);
        view.Magnification = settings.Magnify ? Math.Clamp(settings.Magnification, 1, 3) : 1;
        view.Edge = settings.Edge;
        view.Opacity = settings.Opacity;
        view.Radius = settings.Roundness;
        view.Spacing = settings.Spacing;
        view.Margin = settings.Margin;
        view.Labels = settings.Labels;
        view.Indicator = settings.Indicator;
        view.Bounce = settings.Bounce;
        view.Restyle(Dark);
        screen = DisplayArea.Primary.OuterBounds;
        scale = Native.GetDpiForWindow(hwnd) / 96.0;
        var depth = (int)Math.Ceiling(view.Thickness * scale);
        var rect = settings.Edge switch
        {
            DockEdge.Left => new RectInt32(screen.X, screen.Y, depth, screen.Height),
            DockEdge.Right => new RectInt32(screen.X + screen.Width - depth, screen.Y, depth, screen.Height),
            _ => new RectInt32(screen.X, screen.Y + screen.Height - depth, screen.Width, depth),
        };
        AppWindow.MoveAndResize(rect);
        view.Resize(rect.Width / scale, rect.Height / scale);
        lastShape = "";
        Refresh();
        Animate();
    }

    // Along the dock: across for the bottom edge, down for the sides.
    private double Along(Point p) => settings.Edge == DockEdge.Bottom ? p.X : p.Y;

    private void Point(PointerRoutedEventArgs e)
    {
        view.Pointer = Along(e.GetCurrentPoint(root).Position);
        Animate();
    }

    // Frames run while something moves, and stop once the dock is still.
    private void Animate() => animating = true;

    private void Draw()
    {
        var now = DateTimeOffset.Now;
        view.Tucked += ((Hidden ? 1 : 0) - view.Tucked) * 0.25;
        if (Math.Abs(view.Tucked - (Hidden ? 1 : 0)) < 0.01) view.Tucked = Hidden ? 1 : 0;
        var shapes = view.Frame(now);
        Clip(shapes);
        if (view.Pointer is null && view.Settled && (view.Tucked == 0 || view.Tucked == 1)) animating = false;
    }

    // Only the drawn parts take the mouse: everything else in the strip passes clicks to what's behind.
    private void Clip(List<Rect> shapes)
    {
        var px = shapes.Select(r => new RectInt32((int)Math.Floor(r.X * scale), (int)Math.Floor(r.Y * scale),
                                                  (int)Math.Ceiling(r.Width * scale), (int)Math.Ceiling(r.Height * scale))).ToList();
        var key = string.Join(";", px.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
        if (key == lastShape) return;
        lastShape = key;
        Native.Shape(hwnd, px, (int)Math.Round(settings.Roundness * scale));
    }

    // Tucked away: auto-hide on, the pointer gone for a moment, and no menu open.
    private bool Hidden => settings.AutoHide && view.Pointer is null && DateTimeOffset.Now - pointerAway > TimeSpan.FromSeconds(settings.HideDelay) && !menuOpen;

    // With auto-hide on, the pointer pressed against the dock's edge of the screen brings it out.
    private void WatchEdge()
    {
        if (!settings.AutoHide) return;
        if (Native.Cursor() is { } c && c.X >= screen.X && c.X < screen.X + screen.Width && c.Y >= screen.Y && c.Y < screen.Y + screen.Height
            && settings.Edge switch
            {
                DockEdge.Left => c.X <= screen.X + 1,
                DockEdge.Right => c.X >= screen.X + screen.Width - 2,
                _ => c.Y >= screen.Y + screen.Height - 2,
            })
        {
            pointerAway = DateTimeOffset.Now.AddSeconds(1);
            Animate();
        }
        else if (view.Tucked < 1) Animate();
    }

    // Out of the way while something fills the screen, and back when it doesn't.
    private void WatchFullscreen()
    {
        var hide = settings.HideInFullscreen && settingsWindow is null && Native.FullscreenAppOpen(hwnd);
        if (hide == hiddenForFullscreen) return;
        hiddenForFullscreen = hide;
        if (hide) AppWindow.Hide();
        else AppWindow.Show(false);
    }

    // The items in order: Start, the pins, then apps running that aren't pinned, then Downloads and the Recycle Bin.
    private void Refresh()
    {
        var windows = tasks.Windows();
        var foreground = Native.Foreground();
        var next = new List<DockItem>();
        if (settings.StartButton) next.Add(start);
        DockItem Find(string key) => items.FirstOrDefault(i => i.Key == key) ?? new DockItem { Kind = ItemKind.App, Key = key };

        foreach (var pin in settings.Pins)
        {
            var item = Find(Shell.KeyFor(pin));
            item.Pin = pin;
            item.Label = pin.Name;
            next.Add(item);
        }
        var open = new HashSet<DockItem>();
        foreach (var group in windows.GroupBy(w => w.Key))
        {
            var item = next.FirstOrDefault(i => i.Key == group.Key) ?? next.FirstOrDefault(i => i.Pin is { } pin && Shell.Matches(pin, group.First()));
            if (item is null)
            {
                if (!settings.ShowRunning) continue;
                item = Find(group.Key);
                item.Pin = null;
                item.Label = group.First().AppName;
                next.Add(item);
            }
            // A kept app can gather more than one group (a shortcut matched by program, and its own ID).
            if (open.Add(item)) item.Windows.Clear();
            item.Windows.AddRange(group.Select(w => w.Handle));
            item.Source ??= group.First();
        }
        foreach (var item in next.Where(i => !open.Contains(i))) item.Windows.Clear();
        foreach (var item in next) item.Active = item.Windows.Contains(foreground);

        if (settings.Separator && (settings.Downloads || settings.Trash)) next.Add(separator);
        if (settings.Downloads) next.Add(downloads);
        if (settings.Trash) next.Add(trash);

        var changed = next.Count != items.Count || next.Where((item, i) => items[i] != item).Any();
        items = next;
        foreach (var item in items.Where(i => i.Icon is null && i.Kind != ItemKind.Separator)) _ = LoadIconAsync(item);
        if (trash.Icon is not null && Shell.TrashFull != trashFull) { trashFull = Shell.TrashFull; _ = LoadIconAsync(trash); }
        if (changed)
        {
            view.SetItems(items);
            Animate();
        }
    }

    private bool trashFull;

    private async Task LoadIconAsync(DockItem item)
    {
        var icon = await Icons.ForAsync(item);
        if (icon is null) return;
        item.Icon = icon;
        view.RefreshIcon(item);
        Animate();
    }

    private void OnClick(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(root);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
        if (view.ItemAt(Along(point.Position)) is not { } item) return;
        switch (item.Kind)
        {
            case ItemKind.Trash: Shell.OpenTrash(); break;
            case ItemKind.Start: Shell.OpenStart(); break;
            case ItemKind.Downloads: Shell.OpenDownloads(); break;
            default:
                // As on the Mac: a running app comes to the front, with its most recent window; one that isn't opens.
                if (item.Running && item.Active && settings.ClickMinimizes) Shell.Minimize(item.Windows);
                else if (item.Running) Shell.Activate(item.Windows);
                else if (item.Pin is { } pin && Shell.Launch(pin)) { item.LaunchedAt = DateTimeOffset.Now; Animate(); }
                break;
        }
    }

    private bool menuOpen;

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ShowMenu(e.GetPosition(root));
        e.Handled = true;
    }

    private void ShowMenu(Point at)
    {
        var x = Along(at);
        var menu = new MenuFlyout();
        void Add(string text, Action act, bool check = false, bool isChecked = false)
        {
            MenuFlyoutItemBase entry = check
                ? new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked }
                : new MenuFlyoutItem { Text = text };
            ((MenuFlyoutItem)entry).Click += (_, _) => act();
            menu.Items.Add(entry);
        }
        var item = view.ItemAt(x);
        if (item is { IsApp: true })
        {
            if (item.Windows.Count > 1)
            {
                foreach (var w in item.Windows.Take(8)) Add(Native.Title(w) is { Length: > 0 } t ? t : item.Label, () => Shell.Activate([w]));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            if (item.Running && item.Pin is null) Add("Keep in Dock", () => Pin(item));
            if (item.Pin is not null) Add("Remove from Dock", () => Unpin(item));
            if (item.Pin is { } pin || item.Source is not null) Add("Show in File Explorer", () => Shell.Reveal(item));
            if (!item.Running && item.Pin is { } p) Add("Open", () => { if (Shell.Launch(p)) { item.LaunchedAt = DateTimeOffset.Now; Animate(); } });
            if (item.Running) Add("Quit", () => Shell.Close(item.Windows));
        }
        else if (item is { Kind: ItemKind.Trash })
        {
            Add("Open", Shell.OpenTrash);
            Add("Empty Recycle Bin", Shell.EmptyTrash);
        }
        else if (item is { Kind: ItemKind.Downloads })
        {
            Add("Open Downloads", Shell.OpenDownloads);
        }
        else if (item is { Kind: ItemKind.Start })
        {
            Add("Open Start", Shell.OpenStart);
            Add("Hide the Start button", () => { settings.StartButton = false; Save(); });
        }
        else
        {
            Add("Magnification", () => { settings.Magnify = !settings.Magnify; Save(); }, true, settings.Magnify);
            Add("Automatically hide the Dock", () => { settings.AutoHide = !settings.AutoHide; Save(); }, true, settings.AutoHide);
            var position = new MenuFlyoutSubItem { Text = "Position on screen" };
            foreach (var edge in Enum.GetValues<DockEdge>())
            {
                var entry = new ToggleMenuFlyoutItem { Text = edge.ToString(), IsChecked = settings.Edge == edge };
                entry.Click += (_, _) => { settings.Edge = edge; Save(); };
                position.Items.Add(entry);
            }
            menu.Items.Add(position);
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Dock Settings…", OpenSettings);
            Add("Quit Beanotch Dock", Quit);
        }
        // The dock's settings are a click away from any icon too, not only from its empty ends.
        if (item is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Dock Settings…", OpenSettings);
        }
        menuOpen = true;
        menu.Closed += (_, _) => { menuOpen = false; pointerAway = DateTimeOffset.Now; Animate(); };
        menu.ShowAt(root, at);
    }

    private void Save()
    {
        settings.Save();
        Apply();
    }

    private SettingsWindow? settingsWindow;

    // One settings window at a time; it changes the dock as you go.
    public void OpenSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(settings, this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Activate();
    }

    public void SettingsChanged() => Save();

    public void TaskbarChanged()
    {
        if (settings.HideTaskbar) Taskbar.Hide(settings); else Taskbar.Restore(settings);
        settings.Save();
    }

    private void Pin(DockItem item)
    {
        if (Shell.PinFor(item) is not { } pin) return;
        settings.Pins.Add(pin);
        settings.Save();
        Refresh();
    }

    private void Unpin(DockItem item)
    {
        if (item.Pin is null) return;
        settings.Pins.Remove(item.Pin);
        item.Pin = null;
        settings.Save();
        Refresh();
    }

    // Windows' own taskbar pins, as the dock's first set.
    private void ImportTaskbarPins()
    {
        settings.Pins = Shell.TaskbarPins();
        settings.Imported = true;
        settings.Save();
    }

    // Files dragged over the dock: the Recycle Bin takes them away, an app opens them.
    private void OnDragOver(object sender, DragEventArgs e)
    {
        view.Pointer = Along(e.GetPosition(root));
        Animate();
        var item = view.ItemAt(view.Pointer.Value);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || item is null || item.Kind is ItemKind.Downloads or ItemKind.Separator or ItemKind.Start) return;
        e.AcceptedOperation = item.Kind == ItemKind.Trash ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = item.Kind == ItemKind.Trash ? "Move to Recycle Bin" : "Open with " + item.Label;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var item = view.ItemAt(Along(e.GetPosition(root)));
        if (item is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var paths = (await e.DataView.GetStorageItemsAsync()).Select(f => f.Path).Where(p => p.Length > 0).ToList();
        if (item.Kind == ItemKind.Trash) Shell.Recycle(paths);
        else if (item.IsApp) Shell.OpenWith(item, paths);
    }

    private static void Once(DispatcherQueueTimer timer, TimeSpan after, Action act)
    {
        timer.Interval = after;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => act();
        timer.Start();
    }

    // Checks for a new version; with automatic updates on (or when asked), installs it straight away.
    public async Task CheckForUpdateAsync(bool install)
    {
        await Updater.CheckAsync();
        settingsWindow?.UpdatesChanged();
        if (!Updater.UpdateAvailable || !(install || settings.AutoUpdate)) return;
        if (await Updater.DownloadAndRunAsync()) Quit();
        settingsWindow?.UpdatesChanged();
    }

    private void Quit()
    {
        if (settings.HideTaskbar) Taskbar.Restore(settings);
        Application.Current.Exit();
    }
}
