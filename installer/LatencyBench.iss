; LatencyBench installer — Inno Setup script
; Builds a real Windows installer from the self-contained, single-file Release publish output.
; Requires the Release publish to already exist at PublishDir before compiling:
;   dotnet publish src\LatencyBench.App\LatencyBench.App.csproj -c Release ^
;     -o src\LatencyBench.App\bin\Release\net8.0-windows\win-x64\publish
; (SelfContained/RuntimeIdentifier/PublishSingleFile are set in the csproj itself, so a plain
; -c Release is enough — no extra publish flags needed on the command line.)

#define MyAppName "LatencyBench"
; Canonical version lives in Directory.Build.props (LatencyBenchVersion) at the repo root — this
; string is not read from there automatically (Inno Setup doesn't evaluate MSBuild files), so keep
; the two in sync by hand whenever the version changes. tools/verify-version-sync.ps1 (run in CI)
; fails the build if this drifts from Directory.Build.props or app.manifest.
;
; #ifndef rather than a bare #define: this is the documented way to let `ISCC /DMyAppVersion=X`
; override the value from the command line — a bare #define here would silently win over a /D
; passed before it, since the script's own directives are processed after command-line ones. The
; override exists purely for building a throwaway installer to test the upgrade path against a real
; prior install without hand-editing (and having to remember to revert) the real version everywhere
; else; every normal build still gets exactly 1.0.0 from this line, unchanged.
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
; VersionInfoVersion (used below) is Setup.exe's own Win32 file-version resource - a different
; thing from AppVersion above. Windows enforces a strict numeric "a.b.c.d" format for it (1-4
; parts, no suffixes), while AppVersion is free-form display text. This was originally just
; {#MyAppVersion} reused directly, which broke ISCC compilation the moment a non-numeric override
; was passed for MyAppVersion (e.g. "9.9.9-installertest", used by tools/test-installer.ps1 to
; build a throwaway upgrade-path test installer) - Inno rejects any VersionInfoVersion value that
; isn't purely numeric. A separate, independently-overridable define fixes that without changing
; normal builds: this still defaults to the same numeric version every plain build already used.
#ifndef MyAppFileVersion
  #define MyAppFileVersion "1.1.0.0"
#endif
#define MyAppPublisher "Alisouf"
#define MyAppExeName "LatencyBench.App.exe"
#define PublishDir "..\src\LatencyBench.App\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{B5C2E9A1-7F3D-4E6A-9C1B-2D8F4A6E7C90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Alisouf1/LatencyBench
AppSupportURL=https://github.com/Alisouf1/LatencyBench/issues
; The file/product version shown in Explorer's Properties > Details tab for Setup.exe itself
; (and unins000.exe). See the MyAppFileVersion comment above for why this is a separate define
; from AppVersion rather than {#MyAppVersion} reused directly.
VersionInfoVersion={#MyAppFileVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app's own manifest already requires administrator at launch (registry/ETW/device
; operations need it) — requiring admin for the installer too keeps both consistent and
; lets the installer write to Program Files without a separate elevation prompt mid-install.
PrivilegesRequired=admin
; Refuses to run over an incompatible prior install of a *different* AppId, and — combined with
; AppMutex below — refuses to start while the app itself is running, rather than silently
; overwriting files a running process has open.
AppMutex=LatencyBenchSingleInstanceMutex
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
; --- Code signing --------------------------------------------------------------------------
; No certificate exists yet, so nothing below is active. Once one is available (an EV cert avoids
; SmartScreen's reputation-building delay for new publishers; a standard OV cert works but new
; binaries will show a SmartScreen warning for a while), uncomment SignTool and SignedUninstaller,
; and register the tool ISCC will invoke via Tools > Configure Sign Tools in the Inno Setup IDE, or
; by passing /Ssigntool="..." on the ISCC command line. This signs both Setup.exe and unins000.exe
; — the compiled-in uninstaller — not the payload exe, which is signed separately at publish time
; (see the note in LatencyBench.App.csproj about signing the single-file output before it reaches
; this script's [Files] section).
;SignTool=signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a $f
;SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything from the self-contained, single-file publish output. With PublishSingleFile and
; IncludeNativeLibrariesForSelfExtract set in the csproj, this is normally just the one exe plus
; its .pdb — .pdb is excluded below since end users never need it and it roughly doubles what
; ships for no runtime benefit; keep it in the repo's own build output for crash-dump analysis
; instead, never in the installer.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

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

[Code]
// AppMutex stops Setup running while the app is open; it does not stop the *uninstaller* running
// while the app is open, so without this an uninstall proceeding while the exe is in use fails to
// delete the locked file and the whole uninstall reports failure.
//
// This was first tried as a [UninstallRun] entry running `taskkill`, on the assumption that
// [UninstallRun] entries execute before Inno's own file-removal pass. Tested directly against a
// real running instance: the uninstaller still exited with a failure and the running exe was still
// there afterwards — so that assumption about ordering was wrong, not fixable by changing which
// flags decorate a [UninstallRun] entry. InitializeUninstall's ordering has no such ambiguity: it
// is documented to run before anything else in the uninstall process, which is what actually
// closing the app before file removal begins requires.
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/IM "{#MyAppExeName}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // taskkill's own exit code is deliberately not checked: exit code 128 ("process not found") is
  // the expected, common case where the app was not running at all, not a failure worth stopping
  // the uninstall for. If the process really is still holding the file a moment later, Inno's own
  // file-removal step reports that failure on its own terms instead of this duplicating it.
  Result := True;
end;

[UninstallDelete]
; The app writes its own saved test history/tweak-backup JSON under %LocalAppData%\LatencyBench,
; separate from the install folder — deliberately NOT removed here so uninstalling the app
; doesn't silently delete the user's saved test results. Program-folder contents ARE fully
; removed by Inno's default uninstaller behavior for everything listed under [Files]. The
; self-extraction cache single-file apps create under %TEMP%\.net\LatencyBench.App is left alone
; for the same reason %TEMP% in general is: it is not this installer's directory to own, and
; Windows reclaims stale temp content on its own schedule.
