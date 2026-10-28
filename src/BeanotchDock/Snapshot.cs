using Windows.Foundation;

namespace BeanotchDock;

// With BEANOTCH_DOCK_SNAPSHOT=<folder>, the dock poses for CI: at rest, magnified under a pointer in the middle, and
// with its menu open. It writes "<state>.ready" for each, waits for the workflow's "<state>.done", then quits.
public sealed partial class DockWindow
{
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
            await File.WriteAllTextAsync(Path.Combine(dir, "items.txt"), string.Join(Environment.NewLine,
                items.Select(i => $"{i.Kind} | {i.Label} | {i.Key} | running={i.Running} | icon={i.Icon is not null}")));
            await Pose("rest");
            view.PointerX = view.View.ActualWidth / 2;
            Animate();
            await Pose("magnified");
            view.PointerX = null;
            ShowMenu(new Point(40, view.View.ActualHeight - 30));
            await Pose("menu");
        }
        catch (Exception e) { App.Log(e); }
        Quit();
    }
}
