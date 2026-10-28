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
}
