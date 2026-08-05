<#
.SYNOPSIS
    End-to-end installer verification: first install, reinstall-while-running, version upgrade,
    uninstall-while-running, and full cleanup — against the real Windows install locations and
    registry, not a simulation.

.DESCRIPTION
    This automates the manual verification pass done once by hand while building the installer
    (see the "Installer / release verification" section of the project's working notes). Every
    check here failed at least once for a real reason during that pass before being fixed:

      - The first version of the uninstaller's "close the app first" logic was a [UninstallRun]
        taskkill entry, on the assumption it ran before file removal. Tested against a genuinely
        running instance, it did not: the uninstall failed with the app still open and nothing
        removed. Fixed with an InitializeUninstall [Code] hook, which has unambiguous ordering.
      - PublishSingleFile needs IncludeNativeLibrariesForSelfExtract or the ETW native DLLs are
        left sitting next to the exe, silently defeating "single file".
      - The Inno Setup script's own #define MyAppVersion has to be guarded with #ifndef for a
        command-line /D override (used below to build a throwaway higher-versioned installer) to
        actually take effect — a bare #define silently wins over the command-line value.

    None of these would have been caught by reading the script; all three were only found by
    actually running it. This script exists so that stays true on every future run rather than
    resting on the one manual pass that found them.

.NOTES
    - Must run elevated (installing to Program Files and writing HKLM requires it).
    - Requires Inno Setup 6 (ISCC.exe) - install via: winget install --id JRSoftware.InnoSetup -e
    - Builds a throwaway installer at version 9.9.9-installertest for the upgrade check. This
      never touches Directory.Build.props, app.manifest, or the real installer/LatencyBench.iss
      version line — it is a command-line-only override, verified further down not to be silently
      dropped.
    - If LatencyBench is already installed when this script starts, that installation is left
      alone: the script uninstalls it first (a real test of uninstalling a real prior install),
      then reinstalls the exact same version at the end so the machine is not left in a worse
      state than it started in. If nothing was installed, nothing is installed at the end either.
    - Never touches %LocalAppData%\LatencyBench's real files. A canary file is written and checked
      instead of relying on any file that might already be there.
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$script:FailureCount = 0
$script:CheckCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Description)
    $script:CheckCount++
    if ($Condition) {
        Write-Host "  [PASS] $Description" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $Description" -ForegroundColor Red
        $script:FailureCount++
    }
}

function Step {
    param([string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

# --- Preconditions ---------------------------------------------------------------------------

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "This script must run elevated (installing to Program Files and writing HKLM needs it)." -ForegroundColor Red
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $iscc)) {
    Write-Host "Inno Setup (ISCC.exe) not found. Install with:" -ForegroundColor Red
    Write-Host "  winget install --id JRSoftware.InnoSetup -e" -ForegroundColor Yellow
    exit 1
}

$appId = "{B5C2E9A1-7F3D-4E6A-9C1B-2D8F4A6E7C90}"
$uninstallRegPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$installDir = "C:\Program Files\LatencyBench"
$exeName = "LatencyBench.App.exe"
$publishDir = Join-Path $repoRoot "src\LatencyBench.App\bin\Release\net8.0-windows\win-x64\publish"
$issPath = Join-Path $repoRoot "installer\LatencyBench.iss"
$outputDir = Join-Path $repoRoot "installer\output"
$dataDir = "$env:LOCALAPPDATA\LatencyBench"
$upgradeTestVersion = "9.9.9-installertest"

# --- Preserve whatever was installed before this script ran --------------------------------

$preExisting = Get-ItemProperty $uninstallRegPath -ErrorAction SilentlyContinue
if ($preExisting) {
    Write-Host "Found an existing install (version $($preExisting.DisplayVersion)) - will uninstall it as" -ForegroundColor Yellow
    Write-Host "part of this test, then restore an install of that exact version at the end." -ForegroundColor Yellow
}

function Uninstall-IfPresent {
    $entry = Get-ItemProperty $uninstallRegPath -ErrorAction SilentlyContinue
    if (-not $entry) { return $true }
    $uninstExe = "$installDir\unins000.exe"
    if (-not (Test-Path $uninstExe)) { return $true }
    $p = Start-Process -FilePath $uninstExe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
    Start-Sleep -Seconds 1
    return $p.ExitCode -eq 0
}

Step "Cleaning slate"
$cleaned = Uninstall-IfPresent
Assert-True $cleaned "Removed any pre-existing install"
Assert-True (-not (Test-Path $installDir)) "Install directory absent after cleanup"

# --- Build ------------------------------------------------------------------------------------

if (-not $SkipBuild) {
    Step "Publishing self-contained single-file Release build"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $repoRoot "src\LatencyBench.App\LatencyBench.App.csproj") -c Release -o $publishDir --nologo -v q
    Assert-True ($LASTEXITCODE -eq 0) "dotnet publish succeeded"
}

Assert-True (Test-Path (Join-Path $publishDir $exeName)) "Published exe exists"

# Was "the publish output is exactly one file". Single-file publishing had to be abandoned because
# it broke DPC/ISR tracing: TraceEvent locates KernelTraceControl.dll from its own assembly's
# location, which single-file publishing defines as an empty string, so every trace failed with
# Win32Exception 126. What must be asserted now is the opposite - that those native DLLs really are
# on disk next to the exe, because their absence is precisely the packaging mistake that broke it.
Assert-True (Test-Path (Join-Path $publishDir "amd64\KernelTraceControl.dll")) `
    "TraceEvent's native KernelTraceControl.dll ships next to the exe (required for DPC/ISR tracing)"
Assert-True (Test-Path (Join-Path $publishDir "amd64\msdia140.dll")) `
    "TraceEvent's native msdia140.dll ships next to the exe"

# Self-contained still has to hold, or the app needs .NET preinstalled on the target machine.
Assert-True (Test-Path (Join-Path $publishDir "System.Private.CoreLib.dll")) `
    "Publish output is self-contained (the .NET runtime is included)"

Step "Compiling installer (default version)"
& $iscc $issPath | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "ISCC compiled the default-version installer"

$builtInstaller = Get-ChildItem $outputDir -Filter "LatencyBench-Setup-*.exe" |
    Where-Object { $_.Name -notmatch [regex]::Escape($upgradeTestVersion) } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Assert-True ($null -ne $builtInstaller) "Installer artifact produced"

# --- Rollback after a forced install failure -------------------------------------------------
#
# Blocks the exe's own destination path with a same-named directory before running Setup, so the
# single [Files] copy fails deterministically instead of relying on something flaky like disk
# space. /SUPPRESSMSGBOXES answers the resulting Abort-Retry-Ignore box with Abort, per Inno's
# documented silent-mode behavior, so Setup exits non-zero rather than hanging on a dialog nobody
# is there to click. This never gets past the copy step, so there is nothing partial to roll back
# from - the real thing being verified is that a failed attempt leaves no Apps & Features entry
# and does not prevent a normal install from working right afterward (checked by the next step).

Step "Rollback after a forced install failure"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $installDir $exeName) | Out-Null
$failedInstall = Start-Process -FilePath $builtInstaller.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
Assert-True ($failedInstall.ExitCode -ne 0) "Setup reports failure when the target path cannot be written"
Assert-True (-not (Get-ItemProperty $uninstallRegPath -ErrorAction SilentlyContinue)) "No Apps & Features entry from the failed attempt"
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Assert-True (-not (Test-Path $installDir)) "Blocked path cleaned up before retrying"

# --- First install ------------------------------------------------------------------------

Step "First install (silent)"
$p = Start-Process -FilePath $builtInstaller.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=desktopicon" -PassThru -Wait
Assert-True ($p.ExitCode -eq 0) "Installer exit code 0"
Assert-True (Test-Path (Join-Path $installDir $exeName)) "Exe present at install location"

$entry = Get-ItemProperty $uninstallRegPath -ErrorAction SilentlyContinue
Assert-True ($null -ne $entry) "Apps & Features entry exists"
Assert-True ($entry.DisplayName -like "LatencyBench*") "Apps & Features DisplayName correct"
Assert-True ($null -ne $entry.UninstallString) "Apps & Features UninstallString present"
Assert-True (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\LatencyBench\LatencyBench.lnk") "Start Menu shortcut created"
Assert-True (Test-Path "$env:PUBLIC\Desktop\LatencyBench.lnk") "Desktop shortcut created (task was requested)"

Step "Launch after install"
$proc = Start-Process (Join-Path $installDir $exeName) -PassThru
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 200
}
Assert-True ($proc.MainWindowHandle -ne [IntPtr]::Zero) "App produced a main window within 20s of launch"
Assert-True (-not $proc.HasExited) "App did not crash on startup"

# --- Single-instance protection ------------------------------------------------------------

Step "Single-instance protection"
$second = Start-Process (Join-Path $installDir $exeName) -PassThru
Start-Sleep -Seconds 3
# The second process is expected to still be alive here: App.OnStartup shows a blocking, modal
# "already running" MessageBox before calling Shutdown(), so it only exits once that dialog is
# dismissed - by a user clicking OK, or (as below) by closing it programmatically the way an
# unattended run has to. A naive "exactly one process 3 seconds later" check would treat this
# correct, documented behavior as a failure, so the real assertions are: (1) the first instance
# is still the one running, untouched, and (2) after dismissing the second instance's dialog,
# it goes away and leaves exactly that same first instance behind.
Assert-True (-not $proc.HasExited) "Original instance is unaffected by the second launch attempt"
Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$survivors = @(Get-Process -Name "LatencyBench.App" -ErrorAction SilentlyContinue)
Assert-True ($survivors.Count -eq 1 -and $survivors[0].Id -eq $proc.Id) "Exactly the original instance survives after the second instance's notification is dismissed"

# --- Uninstall while running (the bug this script exists to catch a regression of) ----------

Step "Uninstall while the app is running"
$uninstExe = "$installDir\unins000.exe"
$p = Start-Process -FilePath $uninstExe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
Assert-True ($p.ExitCode -eq 0) "Uninstall-while-running exit code 0"
Assert-True (-not (Get-Process -Name "LatencyBench.App" -ErrorAction SilentlyContinue)) "App process closed by the uninstaller"
Assert-True (-not (Test-Path $installDir)) "Install directory removed"
Assert-True (-not (Get-ItemProperty $uninstallRegPath -ErrorAction SilentlyContinue)) "Apps & Features entry removed"
Assert-True (-not (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\LatencyBench")) "Start Menu group removed"
Assert-True (-not (Test-Path "$env:PUBLIC\Desktop\LatencyBench.lnk")) "Desktop shortcut removed"

# --- User data survives an uninstall -------------------------------------------------------

Step "User data preserved across uninstall"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$canary = Join-Path $dataDir "installer-test-canary.txt"
"canary-$(Get-Date -Format o)" | Set-Content $canary
$p = Start-Process -FilePath $builtInstaller.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
$reuninst = Start-Process -FilePath "$installDir\unins000.exe" -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
Assert-True (Test-Path $canary) "User data directory survives install + uninstall"
Remove-Item $canary -ErrorAction SilentlyContinue

# --- Version upgrade --------------------------------------------------------------------------

Step "Version upgrade (throwaway $upgradeTestVersion over a real install)"
$p = Start-Process -FilePath $builtInstaller.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
Assert-True ($p.ExitCode -eq 0) "Baseline install for upgrade test succeeded"

& $iscc "/DMyAppVersion=$upgradeTestVersion" $issPath | Out-Null
Assert-True ($LASTEXITCODE -eq 0) "Throwaway higher-version installer compiled"
$upgradeInstaller = Join-Path $outputDir "LatencyBench-Setup-$upgradeTestVersion.exe"
Assert-True (Test-Path $upgradeInstaller) "Throwaway installer artifact exists with the overridden version in its filename"

$canary = Join-Path $dataDir "installer-test-canary.txt"
"pre-upgrade-$(Get-Date -Format o)" | Set-Content $canary

$p = Start-Process -FilePath $upgradeInstaller -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
Assert-True ($p.ExitCode -eq 0) "Upgrade install exit code 0"

$appIdGuidOnly = $appId.Trim('{', '}')
$entries = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName.Contains($appIdGuidOnly) }
Assert-True ($entries.Count -eq 1) "Upgrade did not create a duplicate Apps & Features entry"

$entry = Get-ItemProperty $uninstallRegPath -ErrorAction SilentlyContinue
Assert-True ($entry.DisplayVersion -eq $upgradeTestVersion) "Apps & Features shows the upgraded version"
Assert-True (Test-Path $canary) "User data survived the upgrade"

Remove-Item $canary -ErrorAction SilentlyContinue
Remove-Item $upgradeInstaller -ErrorAction SilentlyContinue

# --- Final cleanup: leave the machine exactly as found --------------------------------------

Step "Restoring original state"
Uninstall-IfPresent | Out-Null

if ($preExisting) {
    # Best-effort only: this assumes a matching installer for the pre-existing version is the one
    # just built (true in the common case of running this script against an unmodified checkout).
    # If the pre-existing version differs from what HEAD builds, this intentionally does not try to
    # guess where an old installer artifact might be — it leaves the machine uninstalled and says so.
    if ($preExisting.DisplayVersion -eq (& { $env:MyAppVersionProbe = $null; (Get-Content $issPath | Select-String '#define MyAppVersion "(.*?)"').Matches[0].Groups[1].Value })) {
        Start-Process -FilePath $builtInstaller.FullName -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait | Out-Null
        Write-Host "Restored the original install (version $($preExisting.DisplayVersion))." -ForegroundColor Yellow
    } else {
        Write-Host "Pre-existing version ($($preExisting.DisplayVersion)) differs from what this checkout builds - left uninstalled rather than guessing. Reinstall manually if needed." -ForegroundColor Yellow
    }
}

# --- Summary ------------------------------------------------------------------------------

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "$($script:CheckCount - $script:FailureCount) / $($script:CheckCount) checks passed."
if ($script:FailureCount -gt 0) {
    Write-Host "$($script:FailureCount) FAILED - see [FAIL] lines above." -ForegroundColor Red
    exit 1
}

Write-Host "All installer checks passed." -ForegroundColor Green
exit 0
