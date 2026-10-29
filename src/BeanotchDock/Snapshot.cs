using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BeanotchDock;

// With BEANOTCH_DOCK_SNAPSHOT=<folder>, the dock poses for CI and the website. It keeps a fuller row of apps than a
// fresh build machine has pinned, renders itself to transparent PNGs (light and dark, at rest and magnified), and
// for the desktop shots writes "<state>.ready", waits for the workflow's "<state>.done", then quits.
public sealed partial class DockWindow
{
    // Programs a GitHub Windows runner has, in the order a person might pin them; the missing ones are skipped.
    private static readonly (string Name, string Path, string? Aumid)[] DemoApps =
    [
        ("File Explorer", @"C:\Windows\explorer.exe", "Microsoft.Windows.Explorer"),
        ("Microsoft Edge", "", "MSEdge"),
        ("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null),
        ("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", null),
        ("Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe", null),
        ("Visual Studio", @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe", null),
        ("Android Studio", @"C:\Program Files\Android\Android Studio\bin\studio64.exe", null),
        ("Terminal", "", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
        ("Settings", "", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
    ];

    private async Task SnapshotAsync(string dir)
    {
        async Task Pose(string state)
        {
            await Task.Delay(1500);
            await File.WriteAllTextAsync(Path.Combine(dir, state + ".ready"), "");
            for (var i = 0; i < 40 && !File.Exists(Path.Combine(dir, state + ".done")); i++) await Task.Delay(250);
        }
        try
        {
            settings.Reset();
            Apply();
            // The Store apps are kept by their IDs; the programs only if this machine has them.
            settings.Pins = DemoApps.Where(a => a.Path.Length == 0 || File.Exists(a.Path))
                .Select(a => new Pin { Name = a.Name, Path = a.Path, Aumid = a.Aumid }).ToList();
            // Two apps used lately, for the recent section.
            settings.Recent = [.. new[] { (@"C:\Windows\notepad.exe", "Notepad"), (@"C:\Windows\System32\mspaint.exe", "Paint") }
                .Where(a => File.Exists(a.Item1)).Select(a => new Pin { Name = a.Item2, Path = a.Item1 })];
            // A folder kept as a stack, after Downloads.
            settings.Folders = [.. new[] { @"C:\Windows\Web\Wallpaper" }.Where(Directory.Exists)];
            Refresh();
            await Task.Delay(2500);
            await File.WriteAllTextAsync(Path.Combine(dir, "items.txt"), string.Join(Environment.NewLine,
                items.Select(i => $"{i.Kind} | {i.Label} | {i.Key} | running={i.Running} | icon={i.Icon is not null}")));
            await Pose("rest");
            await Render(dir, "dock-rest");
            // The pet on the bar.
            settings.Pet = true;
            Apply();
            await Task.Delay(1200);
            await Render(dir, "dock-pet");
            settings.Pet = false;
            Apply();
            // Mac tiles, with the dock's own icon chosen for the last app, magnified and at rest.
            if (items.LastOrDefault(i => i.IsApp && !i.Recent) is { } last)
                settings.CustomIcons[IconKey(last)] = Path.Combine(AppContext.BaseDirectory, "Assets", "BeanotchDock.ico");
            settings.IconStyle = DockIconStyle.Tiles;
            Apply();
            await Task.Delay(1500);
            await Render(dir, "dock-tiles");
            var tiled = items.Where(i => i.IsApp).ToList();
            view.Pointer = tiled.Count > 3 ? tiled[3].X + 20 : null;
            Animate();
            await Task.Delay(800);
            await Render(dir, "dock-tiles-magnified");
            view.Pointer = null;
            // The tiles in the dark look: deep instead of pale.
            settings.Theme = DockTheme.Dark;
            Apply();
            await Task.Delay(1500);
            await Render(dir, "dock-tiles-darklook");
            settings.Theme = DockTheme.Auto;
            settings.CustomIcons.Clear();
            settings.IconStyle = DockIconStyle.Original;
            Apply();
            await Task.Delay(1500);
            // The pointer over the fourth icon, as if reaching for it.
            var apps = items.Where(i => i.IsApp).ToList();
            var target = apps.Count > 3 ? apps[3] : apps.LastOrDefault();
            view.Pointer = target is null ? view.View.ActualWidth / 2 : target.X + 20;
            Animate();
            await Pose("magnified");
            await Render(dir, "dock-magnified");
            // An open app's windows, previewed above it.
            if (items.FirstOrDefault(i => i is { IsApp: true, Running: true, Recent: false }) is { } open)
            {
                view.Pointer = open.X + 20;
                Animate();
                await Task.Delay(800);
                ShowPreview(open);
            }
            await Pose("preview");
            HidePreview();
            view.Pointer = null;
            Animate();
            ShowMenu(new Point(40, view.View.ActualHeight - 30));
            await Pose("menu");
            // The Downloads stack, opened from its icon.
            if (items.FirstOrDefault(i => i.Kind == ItemKind.Downloads) is { } stack)
                ShowStack(settings.Edge == DockEdge.Bottom ? new Point(stack.X + 20, view.View.ActualHeight - 30) : new Point(30, stack.X + 20),
                          Services.Shell.DownloadsFolder, "Downloads");
            await Pose("stack");
            OpenSettings();
            // In front of everything and clear of File Explorer, for the photo.
            if (settingsWindow is { } shown)
            {
                if (shown.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.IsAlwaysOnTop = true;
                var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                shown.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width - 700, area.Y + 60, 640, area.Height - 260));
            }
            await Pose("settings");
            // Narrow, to see the controls move under their labels.
            if (settingsWindow is { } narrow)
            {
                var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                narrow.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width - 520, area.Y + 60, 440, area.Height - 260));
            }
            await Pose("narrow");
            settingsWindow?.Close();
            settings.Edge = DockEdge.Left;
            settings.Indicator = DockIndicator.Line;
            Apply();
            view.Pointer = items.Count > 2 ? items[2].X + 20 : null;
            Animate();
            await Pose("left");
        }
        catch (Exception e) { App.Log(e); }
        Quit();
    }

    // The dock alone on a transparent background, at twice its size, in the light look and the dark one.
    private async Task Render(string dir, string name)
    {
        foreach (var dark in new[] { false, true })
        {
            view.Restyle(dark);
            Animate();
            await Task.Delay(400);
            var target = new RenderTargetBitmap();
            await target.RenderAsync(view.View, (int)(view.View.ActualWidth * 2), (int)(view.View.ActualHeight * 2));
            var pixels = await target.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)target.PixelWidth, (uint)target.PixelHeight,
                                 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{name}-{(dark ? "dark" : "light")}.png"), bytes);
        }
        view.Restyle(Dark);
        Animate();
    }
}
