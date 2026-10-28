# Photographs the dock on the build machine for the website: a full-HD desktop with nothing on it but the dock,
# once in Windows' light look and once in its dark one.
param([string]$Out, [string]$Exe)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Desk {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
  public struct DevMode {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public short SpecVersion, DriverVersion, Size, DriverExtra; public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
    public short Color, Duplex, YResolution, TTOption, Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
    public short LogPixels; public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
  }
  [DllImport("user32.dll")] static extern int EnumDisplaySettings(string device, int mode, ref DevMode dm);
  [DllImport("user32.dll")] static extern int ChangeDisplaySettings(ref DevMode dm, int flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int SystemParametersInfo(int action, int param, string value, int flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
  public static int Resolution(int w, int h) {
    var dm = new DevMode(); dm.Size = (short)Marshal.SizeOf(dm);
    EnumDisplaySettings(null, -1, ref dm);
    dm.PelsWidth = w; dm.PelsHeight = h; dm.Fields = 0x80000 | 0x100000;
    return ChangeDisplaySettings(ref dm, 0);
  }
  public static void Wallpaper(string path) { SystemParametersInfo(20, 0, path, 3); }
}
"@

function Shot($path) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save($path)
}

function Look($dark) {
  $personalize = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
  New-Item -Force $personalize | Out-Null
  Set-ItemProperty $personalize AppsUseLightTheme ([int](-not $dark))
  Set-ItemProperty $personalize SystemUsesLightTheme ([int](-not $dark))
  $paper = if ($dark) { "C:\Windows\Web\Wallpaper\Windows\img19.jpg" } else { "C:\Windows\Web\Wallpaper\Windows\img0.jpg" }
  if (Test-Path $paper) { [Desk]::Wallpaper($paper) }
}

"resolution: $([Desk]::Resolution(1920, 1080))"
# No icons on the desktop, and Explorer restarted so it notices.
Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" HideIcons 1
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 5

foreach ($look in "light", "dark") {
  Look ($look -eq "dark")
  # The runner sometimes greets the session with a paging-file notice; close it.
  $notice = [Desk]::FindWindow($null, "System Properties")
  if ($notice -ne [IntPtr]::Zero) { [Desk]::ShowWindow($notice, 0) | Out-Null }
  (New-Object -ComObject Shell.Application).MinimizeAll()
  Start-Sleep -Seconds 2
  $dir = Join-Path $Out $look
  New-Item -ItemType Directory -Force $dir | Out-Null
  # The wallpaper itself, which the website sets the dock on: free of the runner's dialogs and its "Test Mode" mark.
  $paper = if ($look -eq "dark") { "C:\Windows\Web\Wallpaper\Windows\img19.jpg" } else { "C:\Windows\Web\Wallpaper\Windows\img0.jpg" }
  if (Test-Path $paper) { Copy-Item $paper (Join-Path $dir "wall.jpg") }
  $env:BEANOTCH_DOCK_SNAPSHOT = $dir
  $app = Start-Process $Exe -PassThru
  # The dock writes a marker as it reaches each state it wants photographed, and waits for the shot.
  foreach ($state in "rest", "magnified", "menu", "settings", "narrow", "left") {
    $ready = Join-Path $dir "$state.ready"
    for ($i = 0; $i -lt 60 -and -not (Test-Path $ready); $i++) { Start-Sleep -Milliseconds 500 }
    if (Test-Path $ready) { Start-Sleep -Milliseconds 800; Shot (Join-Path $dir "$state.png"); New-Item -ItemType File (Join-Path $dir "$state.done") | Out-Null }
  }
  if (-not $app.WaitForExit(60000)) { Stop-Process $app -Force; "timed out" | Out-File (Join-Path $dir "timeout.txt") }
  "exit code: $($app.ExitCode)" | Out-File (Join-Path $dir "exit.txt")
}
Get-ChildItem -Recurse $Out | Format-Table FullName, Length
