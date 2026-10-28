using Microsoft.Win32;

namespace BeanotchDock.Services;

// Open at sign-in: the same Run entry the installer's checkbox writes.
static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "BeanotchDock";

    public static bool On => Registry.CurrentUser.OpenSubKey(Key)?.GetValue(Name) is string;

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (on) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(Name, false);
    }

    // An app kept in the dock, opened at sign-in, through its own Run entry.
    private static string NameFor(Pin pin) => "BeanotchDock." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(pin.Aumid ?? pin.Path)))[..10];

    public static bool IsOn(Pin pin) => Registry.CurrentUser.OpenSubKey(Key)?.GetValue(NameFor(pin)) is string;

    public static void SetFor(Pin pin, bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!on) { key.DeleteValue(NameFor(pin), false); return; }
        var command = pin.Path.Length > 0 && File.Exists(pin.Path) ? $"\"{pin.Path}\""
            : pin.Aumid is { } id ? $"explorer.exe shell:AppsFolder\\{id}" : null;
        if (command is not null) key.SetValue(NameFor(pin), command);
    }
}
