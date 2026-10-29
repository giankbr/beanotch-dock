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
    private readonly DockItem recentDivider = new() { Kind = ItemKind.Separator, Key = "|recent" };
    // A press on an icon, which becomes a drag once the pointer moves a little.
    private DockItem? pressed;
    private Point pressedAt;
    private bool dragging;
    private readonly DockItem downloads = new() { Kind = ItemKind.Downloads, Key = "downloads", Label = "Downloads" };
    private readonly DockItem trash = new() { Kind = ItemKind.Trash, Key = "trash", Label = "Recycle Bin" };
    private readonly DockItem start = new() { Kind = ItemKind.Start, Key = "start", Label = "Start" };
    private readonly DockItem playing = new() { Kind = ItemKind.NowPlaying, Key = "playing", Label = "Now Playing" };
    private readonly NowPlaying media;
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
        // The Now Playing tile follows the song: its cover is the icon.
        media = new NowPlaying(ui);
        media.Changed += () =>
        {
            playing.Icon = media.Cover ?? Icons.Note;
            view.RefreshIcon(playing);
            Refresh();
            Animate();
        };
        if (SnapshotFolder is null) _ = media.StartAsync();

        root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        root.Children.Add(view.View);
        root.PointerMoved += (_, e) => { Point(e); DragMove(e); };
        root.PointerEntered += (_, e) => Point(e);
        root.PointerExited += (_, _) => { if (pressed is null) { view.Pointer = null; pointerAway = DateTimeOffset.Now; Animate(); } };
        root.PointerPressed += OnPress;
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
        Native.Listen(hwnd);
        Native.Flashed = window => ui.TryEnqueue(() =>
        {
            if (items.FirstOrDefault(i => i.Windows.Contains(window)) is { Active: false } asking) { asking.AttentionAt = DateTimeOffset.Now; Animate(); }
        });
        Native.HotkeyPressed = () => ui.TryEnqueue(() => { settings.AutoHide = !settings.AutoHide; Save(); });
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Beanotch Dock";
        AppWindow.Closing += (_, _) => Quit();

        new UISettings().ColorValuesChanged += (_, _) => ui.TryEnqueue(Restyle);
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

        // The pet strolls along the bar now and then.
        view.WantFrame = Animate;
        var petTimer = ui.CreateTimer();
        petTimer.Interval = TimeSpan.FromSeconds(23);
        petTimer.Tick += (_, _) => { if (Random.Shared.Next(3) > 0) view.Walk(); };
        petTimer.Start();

        // Window previews: watched a few times a second while the pointer is about.
        var previewTimer = ui.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(120);
        previewTimer.Tick += (_, _) => WatchPreview();
        previewTimer.Start();

        // New versions are looked for a minute after start and every six hours.
        updateTimer = ui.CreateTimer();
        updateTimer.Interval = TimeSpan.FromHours(6);
        updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync(false);
        updateTimer.Start();
        if (SnapshotFolder is null) Once(ui.CreateTimer(), TimeSpan.FromMinutes(1), () => _ = CheckForUpdateAsync(false));
        if (SnapshotFolder is null) Announce(ui);

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
        view.PetOn = settings.Pet;
        view.SetPetKind(settings.PetKind);
        Restyle();
        if (iconStyle != settings.IconStyle)
        {
            iconStyle = settings.IconStyle;
            ReloadIcons();
        }
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

        // An open app that isn't kept, closed: it joins the recent ones (the newest three), as on the Mac.
        foreach (var gone in items.Where(i => i.IsApp && !i.Recent && i.Pin is null && i.Source is not null && !next.Contains(i)))
            if (Shell.PinFor(gone) is { } was && !settings.Pins.Any(p => Shell.KeyFor(p) == gone.Key))
            {
                settings.Recent.RemoveAll(r => Shell.KeyFor(r) == gone.Key);
                settings.Recent.Insert(0, was);
                if (settings.Recent.Count > 3) settings.Recent.RemoveRange(3, settings.Recent.Count - 3);
                settings.Save();
            }
        if (settings.ShowRecent)
        {
            var recent = settings.Recent.Where(r => !next.Any(i => i.Key == Shell.KeyFor(r))).ToList();
            if (recent.Count > 0) next.Add(recentDivider);
            foreach (var pin in recent)
            {
                var item = Find("recent:" + Shell.KeyFor(pin));
                item.Pin = pin;
                item.Recent = true;
                item.Label = pin.Name;
                next.Add(item);
            }
        }

        // What's playing, after the apps: its cover is the icon.
        if (settings.NowPlaying && media.HasTrack)
        {
            playing.Label = media.Artist.Length > 0 ? $"{media.Title} — {media.Artist}" : media.Title;
            next.Add(playing);
        }

        var folders = settings.Folders.Where(Directory.Exists).ToList();
        if (settings.Separator && (settings.Downloads || settings.Trash || folders.Count > 0)) next.Add(separator);
        if (settings.Downloads) next.Add(downloads);
        foreach (var folder in folders)
            next.Add(items.FirstOrDefault(i => i.Key == "folder:" + folder)
                     ?? new DockItem { Kind = ItemKind.Folder, Key = "folder:" + folder, Folder = folder, Label = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder });
        if (settings.Trash) next.Add(trash);

        var changed = next.Count != items.Count || next.Where((item, i) => items[i] != item).Any();
        items = next;
        foreach (var item in items.Where(i => i.Icon is null && i.Kind is not (ItemKind.Separator or ItemKind.NowPlaying))) _ = LoadIconAsync(item);
        if (trash.Icon is not null && Shell.TrashFull != trashFull) { trashFull = Shell.TrashFull; _ = LoadIconAsync(trash); }
        if (changed)
        {
            view.SetItems(items);
            Animate();
        }
    }

    private bool trashFull;
    private PreviewWindow? preview;
    private DockItem? previewFor, hoverItem;
    private DateTimeOffset hoverSince;

    // After the pointer rests on an open app (at the bottom edge), its windows' pictures show above it, and stay while
    // the pointer is on them or moves to another open app; anywhere else they go.
    private void WatchPreview()
    {
        var hovered = view.Hovered;
        if (hovered != hoverItem) { hoverItem = hovered; hoverSince = DateTimeOffset.Now; }
        var settled = previewFor is not null || DateTimeOffset.Now - hoverSince > TimeSpan.FromMilliseconds(550);
        var wanted = settings.WindowPreviews && settings.Edge == DockEdge.Bottom && !dragging && !menuOpen && view.Tucked == 0
                     && hovered is { IsApp: true, Running: true, Recent: false } && settled;
        if (wanted && hovered != previewFor)
        {
            ShowPreview(hovered!);
            return;
        }
        if (wanted || previewFor is null || preview is null) return;
        var b = preview.Bounds;
        if (Native.Cursor() is { } c && c.X >= b.X - 12 && c.X <= b.X + b.Width + 12 && c.Y >= b.Y - 12 && c.Y <= b.Y + b.Height + 16) return;
        HidePreview();
    }

    private void ShowPreview(DockItem item)
    {
        preview ??= new PreviewWindow { Picked = w => { Shell.Activate([w]); HidePreview(); } };
        var at = AppWindow.Position;
        var centre = at.X + (int)Math.Round(view.CentreOf(item) * scale);
        var bottom = at.Y + (int)Math.Round((view.View.ActualHeight - view.AboveIcons) * scale);
        preview.Show(item.Windows, centre, bottom, scale, Dark);
        previewFor = item;
        view.Quiet = true;
        Animate();
    }

    private void HidePreview()
    {
        preview?.Hide();
        previewFor = null;
        view.Quiet = false;
        Animate();
    }
    private DockIconStyle? iconStyle;
    private bool? tilesDark;

    // The glass in the light or dark look, and the tiles with it: pale in the light look, deep in the dark one.
    private void Restyle()
    {
        var dark = Dark;
        view.Restyle(dark);
        if (tilesDark == dark) return;
        var first = tilesDark is null;
        tilesDark = dark;
        if (first || settings.IconStyle != DockIconStyle.Tiles) return;
        ReloadIcons();
        Refresh();
    }

    // Every icon drawn again on the next refresh, after the style changes.
    private void ReloadIcons()
    {
        foreach (var item in items.Append(start).Append(downloads).Append(trash)) item.Icon = null;
    }

    // What a picture you chose is kept under: a recent app shares its kept self's.
    private static string IconKey(DockItem item) => item.Recent && item.Pin is { } pin ? Shell.KeyFor(pin) : item.Key;

    private async Task LoadIconAsync(DockItem item)
    {
        var icon = await Icons.ForAsync(item, settings.CustomIcons.GetValueOrDefault(IconKey(item)), settings.IconStyle == DockIconStyle.Tiles, Dark);
        if (icon is null) return;
        item.Icon = icon;
        view.RefreshIcon(item);
        Animate();
    }

    private void OnPress(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(root);
        if (!point.Properties.IsLeftButtonPressed) return;
        pressed = view.ItemAt(Along(point.Position));
        pressedAt = point.Position;
        dragging = false;
        if (pressed is not null) root.CapturePointer(e.Pointer);
    }

    // How far the pointer is from the screen's edge the dock sits on.
    private double OutFromEdge(Point p) => settings.Edge switch
    {
        DockEdge.Left => p.X,
        DockEdge.Right => view.View.ActualWidth - p.X,
        _ => view.View.ActualHeight - p.Y,
    };

    // Dragging an app along the dock moves it among the kept ones; pulling it well off the dock takes it out.
    private void DragMove(PointerRoutedEventArgs e)
    {
        if (pressed is not { IsApp: true, Recent: false } item) return;
        var p = e.GetCurrentPoint(root).Position;
        if (!dragging)
        {
            if (Math.Abs(p.X - pressedAt.X) + Math.Abs(p.Y - pressedAt.Y) < 8) return;
            dragging = true;
            if (item.Pin is null && Shell.PinFor(item) is { } pin) { settings.Pins.Add(pin); item.Pin = pin; }
            view.Dragging = item;
        }
        view.DragAway = OutFromEdge(p) > settings.Margin + view.BarThickness + view.IconSize * 1.3;
        if (!view.DragAway && item.Pin is { } kept)
        {
            // The place among the kept apps nearest the pointer.
            var along = Along(p);
            var others = items.Where(i => i.Pin is not null && !i.Recent && i.IsApp).ToList();
            var target = others.Count(i => i != item && i.X + view.IconSize / 2 < along);
            var now = settings.Pins.IndexOf(kept);
            if (now >= 0 && target != now)
            {
                settings.Pins.RemoveAt(now);
                settings.Pins.Insert(Math.Clamp(target, 0, settings.Pins.Count), kept);
                Refresh();
            }
        }
        Animate();
    }

    private void OnClick(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(root);
        root.ReleasePointerCaptures();
        var wasDragging = dragging;
        dragging = false;
        pressed = null;
        if (wasDragging)
        {
            if (view.Dragging is { } dropped && view.DragAway) Unpin(dropped);
            view.Dragging = null;
            view.DragAway = false;
            settings.Save();
            Refresh();
            Animate();
            return;
        }
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
        // A tap on the pet makes it hop, and opens nothing.
        if (view.OnPet(point.Position)) { view.Hop(); return; }
        if (view.ItemAt(Along(point.Position)) is not { } item) return;
        switch (item.Kind)
        {
            case ItemKind.Trash: Shell.OpenTrash(); break;
            case ItemKind.Start: Shell.OpenStart(); break;
            case ItemKind.Downloads:
                if (settings.DownloadsStack) ShowStack(point.Position, Shell.DownloadsFolder, "Downloads"); else Shell.OpenDownloads();
                break;
            case ItemKind.Folder: ShowStack(point.Position, item.Folder!, item.Label); break;
            case ItemKind.NowPlaying: media.TogglePlay(); break;
            default:
                // As on the Mac: a running app comes to the front, with its most recent window; one that isn't opens.
                if (item.Running && item.Active && settings.ClickMinimizes) Shell.Minimize(item.Windows);
                else if (item.Running) Shell.Activate(item.Windows);
                else if (item.Pin is { } pin && Shell.Launch(pin)) { item.LaunchedAt = DateTimeOffset.Now; view.Hop(); Animate(); }
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
            // Options, as on the Mac: keep it, open it at sign-in, find it.
            var options = new MenuFlyoutSubItem { Text = "Options" };
            void Option(string text, Action act, bool? isChecked = null)
            {
                MenuFlyoutItem entry = isChecked is { } on ? new ToggleMenuFlyoutItem { Text = text, IsChecked = on } : new MenuFlyoutItem { Text = text };
                entry.Click += (_, _) => act();
                options.Items.Add(entry);
            }
            var kept = item.Pin is not null && !item.Recent;
            Option("Keep in Dock", () => { if (kept) Unpin(item); else Pin(item); }, kept);
            if ((item.Pin ?? Shell.PinFor(item)) is { } launch) Option("Open at Login", () => Startup.SetFor(launch, !Startup.IsOn(launch)), Startup.IsOn(launch));
            if (item.Pin is not null || item.Source is not null) Option("Show in File Explorer", () => Shell.Reveal(item));
            options.Items.Add(new MenuFlyoutSeparator());
            Option("Change Icon…", () => _ = ChangeIconAsync(item));
            if (settings.CustomIcons.ContainsKey(IconKey(item))) Option("Use Original Icon", () => SetIcon(item, null));
            menu.Items.Add(options);
            menu.Items.Add(new MenuFlyoutSeparator());
            if (!item.Running && item.Pin is { } p) Add("Open", () => { if (Shell.Launch(p)) { item.LaunchedAt = DateTimeOffset.Now; Animate(); } });
            if (item.Running) Add("Hide", () => Shell.Minimize(item.Windows));
            if (item.Running) Add("Quit", () => Shell.Close(item.Windows));
        }
        else if (item is { Kind: ItemKind.Trash })
        {
            Add("Open", Shell.OpenTrash);
            Add("Empty Recycle Bin", Shell.EmptyTrash);
        }
        else if (item is { Kind: ItemKind.Downloads })
        {
            Add("Open in File Explorer", Shell.OpenDownloads);
            Add("Display as Stack", () => { settings.DownloadsStack = true; settings.Save(); }, true, settings.DownloadsStack);
            Add("Display as Folder", () => { settings.DownloadsStack = false; settings.Save(); }, true, !settings.DownloadsStack);
        }
        else if (item is { Kind: ItemKind.Folder, Folder: { } folder })
        {
            Add("Open in File Explorer", () => Native.Open(folder));
            Add("Remove from Dock", () => { settings.Folders.Remove(folder); Save(); });
        }
        else if (item is { Kind: ItemKind.NowPlaying })
        {
            Add(media.Playing ? "Pause" : "Play", media.TogglePlay);
            Add("Next", media.Next);
            Add("Previous", media.Previous);
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Hide Now Playing", () => { settings.NowPlaying = false; Save(); });
        }
        else if (item is { Kind: ItemKind.Start })
        {
            Add("Open Start", Shell.OpenStart);
            Add("Hide the Start button", () => { settings.StartButton = false; Save(); });
        }
        else
        {
            Add("Turn Magnification " + (settings.Magnify ? "Off" : "On"), () => { settings.Magnify = !settings.Magnify; Save(); });
            Add("Turn Hiding " + (settings.AutoHide ? "Off" : "On") + "  (Ctrl+Alt+D)", () => { settings.AutoHide = !settings.AutoHide; Save(); });
            var position = new MenuFlyoutSubItem { Text = "Position on screen" };
            foreach (var edge in Enum.GetValues<DockEdge>())
            {
                var entry = new ToggleMenuFlyoutItem { Text = edge.ToString(), IsChecked = settings.Edge == edge };
                entry.Click += (_, _) => { settings.Edge = edge; Save(); };
                position.Items.Add(entry);
            }
            menu.Items.Add(position);
            Add("Add Folder to Dock…", () => _ = AddFolderAsync());
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

    // Any picture as an app's icon (a PNG, an ICO, a JPEG), copied beside the settings so it stays if the file moves.
    private async Task ChangeIconAsync(DockItem item)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        foreach (var type in new[] { ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        try
        {
            var folder = Path.Combine(DockSettings.Folder, "Icons");
            Directory.CreateDirectory(folder);
            // A new name each time, so the icon cache never hands back the old picture.
            var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(IconKey(item))))[..10];
            var copy = Path.Combine(folder, $"{name}-{DateTime.Now.Ticks}{Path.GetExtension(file.Path).ToLowerInvariant()}");
            File.Copy(file.Path, copy, true);
            SetIcon(item, copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(e); }
    }

    // A chosen picture for the app, or none to go back to its own icon.
    private void SetIcon(DockItem item, string? path)
    {
        var key = IconKey(item);
        if (settings.CustomIcons.Remove(key, out var old))
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(e); }
        if (path is not null) settings.CustomIcons[key] = path;
        settings.Save();
        foreach (var same in items.Where(i => IconKey(i) == key)) same.Icon = null;
        Refresh();
    }

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

    // A folder kept in the dock, picked from the file dialog.
    private async Task AddFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        AddFolders([folder.Path]);
    }

    private void AddFolders(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var path in paths.Where(Directory.Exists))
            if (!settings.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) { settings.Folders.Add(path); added = true; }
        if (added) Save();
    }

    // A folder as a stack: its newest files in a grid above the dock, as on the Mac.
    private void ShowStack(Point at, string folder, string title)
    {
        var files = Shell.Newest(folder, 12);
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4, ItemWidth = 96, ItemHeight = 104 };
        // In its own window: the dock's is cut to the bar's shape and would hide most of it.
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };
        foreach (var file in files)
        {
            var tile = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            tile.Children.Add(new Image { Width = 56, Height = 56, Source = Icons.ForFile(file) });
            tile.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(file), FontSize = 11.5, TextAlignment = TextAlignment.Center,
                                             TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap, Width = 88 });
            var button = new Button { Content = tile, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4) };
            button.Click += (_, _) => { flyout.Hide(); Native.Open(file); };
            grid.Children.Add(button);
        }
        var panel = new StackPanel { Spacing = 10, Width = 400 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (files.Count == 0) panel.Children.Add(new TextBlock { Text = "Nothing here yet", Opacity = 0.6 });
        else panel.Children.Add(grid);
        var open = new HyperlinkButton { Content = "Open in File Explorer" };
        open.Click += (_, _) => { flyout.Hide(); Native.Open(folder); };
        panel.Children.Add(open);
        flyout.Content = panel;
        menuOpen = true;
        flyout.Closed += (_, _) => { menuOpen = false; pointerAway = DateTimeOffset.Now; Animate(); };
        flyout.ShowAt(root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
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
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        // Over the dock's own end (a divider, Downloads, a folder or the bar between): keep a dropped folder as a stack.
        if (item is null or { Kind: ItemKind.Separator or ItemKind.Downloads or ItemKind.Folder })
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Keep folders in Dock";
            return;
        }
        if (item.Kind is ItemKind.Start or ItemKind.NowPlaying) return;
        e.AcceptedOperation = item.Kind == ItemKind.Trash ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = item.Kind == ItemKind.Trash ? "Move to Recycle Bin" : "Open with " + item.Label;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var item = view.ItemAt(Along(e.GetPosition(root)));
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var paths = (await e.DataView.GetStorageItemsAsync()).Select(f => f.Path).Where(p => p.Length > 0).ToList();
        if (item is null or { Kind: ItemKind.Separator or ItemKind.Downloads or ItemKind.Folder })
        {
            AddFolders(paths);
            return;
        }
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

    // Windows notifications for updates: after one just installed itself, say so once, with the release notes a
    // click away; with automatic updates off, the buttons on a new version's notice install it.
    private void Announce(DispatcherQueue ui)
    {
        Toast.Register(ui);
        Toast.Invoked = action =>
        {
            if (action == "update") _ = CheckForUpdateAsync(true);
            else if (action == "news")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://github.com/giankbr/beanotch-dock/releases/tag/v{Updater.Current}") { UseShellExecute = true });
        };
        if (settings.LastVersion.Length > 0 && settings.LastVersion != Updater.Current)
            Toast.Show($"Beanotch Dock updated to {Updater.Current}", "The dock updated itself. See what's new in this version.", "What's new", "news");
        if (settings.LastVersion != Updater.Current)
        {
            settings.LastVersion = Updater.Current;
            settings.Save();
        }
    }

    // Checks for a new version; with automatic updates on (or when asked), installs it straight away. With them off, a
    // new version is announced once, in a notification with an Update button.
    public async Task CheckForUpdateAsync(bool install)
    {
        await Updater.CheckAsync();
        settingsWindow?.UpdatesChanged();
        if (Updater.UpdateAvailable && !install && !settings.AutoUpdate && settings.AnnouncedVersion != Updater.Latest)
        {
            settings.AnnouncedVersion = Updater.Latest!;
            settings.Save();
            Toast.Show($"Beanotch Dock {Updater.Latest} is out", "Update now, and the dock is back a few seconds later.", "Update", "update");
        }
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
