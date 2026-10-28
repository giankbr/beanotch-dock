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

public enum DockSize { Small, Medium, Large }

// Saved as JSON in %LOCALAPPDATA%\BeanotchDock\settings.json.
public sealed class DockSettings
{
    public List<Pin> Pins { get; set; } = [];
    public bool Imported { get; set; }
    public DockSize Size { get; set; } = DockSize.Medium;
    public bool Magnify { get; set; } = true;
    public bool AutoHide { get; set; }
    // Windows' own taskbar slides away while the dock runs, and comes back when it quits.
    public bool HideTaskbar { get; set; } = true;
    // Whether the taskbar was already set to hide itself before the dock changed it, so quitting leaves it as found.
    public bool? TaskbarWasAutoHide { get; set; }
    public bool Downloads { get; set; } = true;

    public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeanotchDock");
    private static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    public double IconSize => Size switch { DockSize.Small => 40, DockSize.Large => 64, _ => 52 };

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
