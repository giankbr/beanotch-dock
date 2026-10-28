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
        ("Notepad", @"C:\Windows\notepad.exe", null),
        ("Paint", @"C:\Windows\System32\mspaint.exe", null),
        ("Command Prompt", @"C:\Windows\System32\cmd.exe", null),
        ("PowerShell", @"C:\Program Files\PowerShell\7\pwsh.exe", null),
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
            settings.Pins = DemoApps.Where(a => a.Path.Length == 0 || File.Exists(a.Path))
                .Select(a => new Pin { Name = a.Name, Path = a.Path, Aumid = a.Aumid }).ToList();
            Refresh();
            await Task.Delay(2500);
            await File.WriteAllTextAsync(Path.Combine(dir, "items.txt"), string.Join(Environment.NewLine,
                items.Select(i => $"{i.Kind} | {i.Label} | {i.Key} | running={i.Running} | icon={i.Icon is not null}")));
            await Pose("rest");
            await Render(dir, "dock-rest");
            // The pointer over the fourth icon, as if reaching for it.
            var apps = items.Where(i => i.IsApp).ToList();
            var target = apps.Count > 3 ? apps[3] : apps.LastOrDefault();
            view.Pointer = target is null ? view.View.ActualWidth / 2 : target.X + view.IconSize / 2;
            Animate();
            await Pose("magnified");
            await Render(dir, "dock-magnified");
            view.Pointer = null;
            Animate();
            ShowMenu(new Point(40, view.View.ActualHeight - 30));
            await Pose("menu");
            OpenSettings();
            await Pose("settings");
            settingsWindow?.Close();
            settings.Edge = DockEdge.Left;
            settings.Indicator = DockIndicator.Line;
            Apply();
            view.Pointer = items.Count > 2 ? items[2].X + view.IconSize / 2 : null;
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
