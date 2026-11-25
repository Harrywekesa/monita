#define AppName "Monita"
#define AppVersion "1.0.0"
#define AppExeName "TrafficMonitor.exe"

[Setup]
AppId={{FC4AF85C-8E2B-4ED9-BA3A-7C3A8E8B4F1D}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\Monita
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\monita.ico
OutputDir=..\artifacts
OutputBaseFilename=Monita-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MonitaTrafficMonitor"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMonitaStartupTask"
