; Builds BeanotchDock-Setup-<arch>.exe from the published dock in out\BeanotchDock.
; iscc /DAppVersion=0.1.0 /DArch=x64 installer\BeanotchDock.iss
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{B3E1F0A2-4C57-4D8E-9A61-2F7D3C9E5B14}
AppName=Beanotch Dock
AppVersion={#AppVersion}
AppPublisher=Gian
AppPublisherURL=https://giankbr.github.io/beanie-notch
AppSupportURL=https://github.com/giankbr/beanie-notch/issues
; Per-user install: no admin prompt, and updates can replace it later without one.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\BeanotchDock
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\out
OutputBaseFilename=BeanotchDock-Setup-{#Arch}
SetupIconFile=..\src\BeanotchDock\Assets\BeanotchDock.ico
UninstallDisplayIcon={app}\BeanotchDock.exe
UninstallDisplayName=Beanotch Dock
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "startup"; Description: "Open Beanotch Dock when I sign in"

[Files]
Source: "..\out\BeanotchDock\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{userprograms}\Beanotch Dock"; Filename: "{app}\BeanotchDock.exe"

; The same Run entry the dock's menu turns on and off.
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BeanotchDock"; ValueData: """{app}\BeanotchDock.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\BeanotchDock.exe"; Description: "Open Beanotch Dock"; Flags: nowait postinstall skipifsilent
; An update from inside the app runs this quietly; open the new version when it's done.
Filename: "{app}\BeanotchDock.exe"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\BeanotchDock"
