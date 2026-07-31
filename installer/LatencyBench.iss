; LatencyBench installer — Inno Setup script
; Builds a real Windows installer from the self-contained Release publish output.
; Requires the Release publish to already exist at PublishDir before compiling.

#define MyAppName "LatencyBench"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Alisouf"
#define MyAppExeName "LatencyBench.App.exe"
#define PublishDir "..\src\LatencyBench.App\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{B5C2E9A1-7F3D-4E6A-9C1B-2D8F4A6E7C90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app's own manifest already requires administrator at launch (registry/ETW/device
; operations need it) — requiring admin for the installer too keeps both consistent and
; lets the installer write to Program Files without a separate elevation prompt mid-install.
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=LatencyBench-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
; The setup executable's own icon, and the icon shown in Apps & features / Programs and Features.
SetupIconFile=..\src\LatencyBench.App\Assets\LatencyBench.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything from the self-contained publish output — the whole .NET runtime, WPF, and the
; native ETW tracing DLLs (KernelTraceControl etc.) the app needs, not just the exe.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launched via explorer.exe rather than directly, because Setup.exe itself runs elevated
; (PrivilegesRequired=admin) and LatencyBench.App.exe's own manifest ALSO requires elevation —
; asking an already-elevated process to CreateProcess a second manifest-elevated target fails
; with error 740 ("The requested operation requires elevation"). explorer.exe runs at medium
; integrity as the interactive user and uses ShellExecute internally, which correctly triggers
; a fresh UAC prompt for the target's own manifest instead of hitting that failure.
Filename: "{win}\explorer.exe"; Parameters: """{app}\{#MyAppExeName}"""; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app writes its own saved test history/tweak-backup JSON under %LocalAppData%\LatencyBench,
; separate from the install folder — deliberately NOT removed here so uninstalling the app
; doesn't silently delete the user's saved test results. Program-folder contents ARE fully
; removed by Inno's default uninstaller behavior for everything listed under [Files].
