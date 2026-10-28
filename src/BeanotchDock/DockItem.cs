using Microsoft.UI.Xaml.Media;

namespace BeanotchDock;

public enum ItemKind { App, Separator, Downloads, Trash }

// One thing in the dock: an app (kept, running, or both), the line before the stacks, Downloads, or the Recycle Bin.
public sealed class DockItem
{
    public ItemKind Kind { get; init; }
    // What groups an app's windows and its pin: its AppUserModelID, or its program path in lower case.
    public string Key { get; init; } = "";
    public string Label { get; set; } = "";
    public Pin? Pin { get; set; }
    public ImageSource? Icon { get; set; }
    public List<nint> Windows { get; } = [];
    public bool Active { get; set; }
    // A launch in progress: it bounces until a window shows up, or for a few seconds.
    public DateTimeOffset? LaunchedAt { get; set; }
    // Where it sits along the dock, eased toward its target each frame.
    public double Scale { get; set; } = 1;
    public double X { get; set; }

    public bool Running => Windows.Count > 0;
    public bool IsApp => Kind == ItemKind.App;
}
