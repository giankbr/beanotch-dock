using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeanotchDock;

// An app kept in the dock: a shortcut or program path, or a Store app's AppUserModelID.
public sealed class Pin
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Aumid { get; set; }
}

public enum DockEdge { Bottom, Left, Right }
public enum DockTheme { Auto, Light, Dark }
public enum DockIndicator { Dot, Line, None }
public enum DockIconStyle { Original, Tiles }

// Saved as JSON in %LOCALAPPDATA%\BeanotchDock\settings.json. Every field has a sensible default, so a file from an
// older version (or none) still loads.
public sealed class DockSettings
{
    public List<Pin> Pins { get; set; } = [];
    public bool Imported { get; set; }

    // Appearance
    public double IconSize { get; set; } = 52;
    public bool Magnify { get; set; } = true;
    public double Magnification { get; set; } = 1.7;
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    public DockTheme Theme { get; set; } = DockTheme.Auto;
    public double Opacity { get; set; } = 0.8;
    public double Roundness { get; set; } = 20;
    public double Spacing { get; set; } = 0.1;
    public double Margin { get; set; } = 8;
    public bool Labels { get; set; } = true;
    public DockIndicator Indicator { get; set; } = DockIndicator.Dot;
    // Icons as Windows draws them, or every app on a rounded square, as on the Mac.
    public DockIconStyle IconStyle { get; set; } = DockIconStyle.Original;
    // Pictures you chose for apps, by the app's key: copies kept in the Icons folder beside this file.
    public Dictionary<string, string> CustomIcons { get; set; } = [];

    // Behaviour
    public bool AutoHide { get; set; }
    // Seconds the dock waits, once the pointer leaves, before tucking itself away.
    public double HideDelay { get; set; } = 0.6;
    public bool Bounce { get; set; } = true;
    // Apps that are open but not kept in the dock still show, after the kept ones.
    public bool ShowRunning { get; set; } = true;
    // Windows' habit: clicking the app in front minimizes it. Off, as on the Mac: it stays.
    public bool ClickMinimizes { get; set; }
    // Out of the way while a game, a video or a slideshow fills the screen.
    public bool HideInFullscreen { get; set; } = true;
    // Live pictures of an app's windows above its icon when the pointer rests on it.
    public bool WindowPreviews { get; set; } = true;

    // Items
    // Windows' Start button at the head of the dock, since the taskbar that holds it is out of sight.
    public bool StartButton { get; set; } = true;
    public bool Downloads { get; set; } = true;
    // As on the Mac: Downloads opens as a stack of its newest files, or as the folder.
    public bool DownloadsStack { get; set; } = true;
    // "Show suggested and recent apps in Dock": the last few apps you closed that aren't kept, after a divider.
    public bool ShowRecent { get; set; } = true;
    public List<Pin> Recent { get; set; } = [];
    public bool Trash { get; set; } = true;
    // Folders kept in the dock as stacks, after Downloads, as on the Mac.
    public List<string> Folders { get; set; } = [];
    // A tile for what's playing, its cover as the icon: a click plays or pauses.
    public bool NowPlaying { get; set; } = true;
    // A pixel friend on top of the dock, as in Beanotch's notch: bean, cat, frog, ghost, dog, panda, bunny or robot.
    public bool Pet { get; set; }
    public string PetKind { get; set; } = "bean";
    public bool Separator { get; set; } = true;

    // New versions download and install themselves, with the dock back a few seconds later.
    public bool AutoUpdate { get; set; } = true;
    // The version that last ran, so the first start after an update can say so, and the newest one announced while
    // automatic updates are off, so each is announced once.
    public string LastVersion { get; set; } = "";
    public string AnnouncedVersion { get; set; } = "";

    // Windows' own taskbar slides away while the dock runs, and comes back when it quits.
    public bool HideTaskbar { get; set; } = true;
    // Whether the taskbar was already set to hide itself before the dock changed it, so quitting leaves it as found.
    public bool? TaskbarWasAutoHide { get; set; }

    // Back to how a fresh install looks, keeping the pins, the icons you chose and what the taskbar was.
    public void Reset()
    {
        var d = new DockSettings();
        (IconSize, Magnify, Magnification, Edge, Theme, Opacity, Roundness, Spacing, Margin, Labels, Indicator, IconStyle) =
            (d.IconSize, d.Magnify, d.Magnification, d.Edge, d.Theme, d.Opacity, d.Roundness, d.Spacing, d.Margin, d.Labels, d.Indicator, d.IconStyle);
        (AutoHide, HideDelay, Bounce, ShowRunning, ClickMinimizes, HideInFullscreen) =
            (d.AutoHide, d.HideDelay, d.Bounce, d.ShowRunning, d.ClickMinimizes, d.HideInFullscreen);
        (StartButton, Downloads, Trash, Separator, DownloadsStack, ShowRecent) =
            (d.StartButton, d.Downloads, d.Trash, d.Separator, d.DownloadsStack, d.ShowRecent);
    }

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeanotchDock");
    private static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    public static DockSettings Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(FilePath), DockJson.Default.DockSettings) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, DockJson.Default.DockSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log(e); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DockSettings))]
internal sealed partial class DockJson : JsonSerializerContext;
