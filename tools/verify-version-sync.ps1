<#
.SYNOPSIS
    Fails if the app version in installer/LatencyBench.iss or app.manifest has drifted from the
    canonical LatencyBenchVersion in Directory.Build.props.

.DESCRIPTION
    Directory.Build.props is the single source of truth for the app's version (see the comment
    there) and is picked up automatically by every csproj. installer/LatencyBench.iss and
    src/LatencyBench.App/app.manifest are NOT MSBuild-evaluated, so neither picks it up
    automatically — both hardcode a matching string by hand instead, with a comment pointing back
    at Directory.Build.props. This script is the CI-side guard against those two silently drifting
    out of sync after a version bump touches one file but not the others.
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$issPath = Join-Path $repoRoot "installer\LatencyBench.iss"
$manifestPath = Join-Path $repoRoot "src\LatencyBench.App\app.manifest"

function Read-RequiredMatch {
    param(
        [string]$Path,
        [string]$Pattern,
        [string]$Description
    )

    if (-not (Test-Path $Path)) {
        Write-Host "::error::Could not find $Path."
        exit 1
    }

    $content = Get-Content -Path $Path -Raw
    $match = [regex]::Match($content, $Pattern)
    if (-not $match.Success) {
        Write-Host "::error::Could not find $Description in $Path."
        exit 1
    }

    return $match.Groups[1].Value
}

$propsVersion = Read-RequiredMatch -Path $propsPath -Pattern "<LatencyBenchVersion>(.*?)</LatencyBenchVersion>" -Description "LatencyBenchVersion"
$issVersion = Read-RequiredMatch -Path $issPath -Pattern '#define MyAppVersion "(.*?)"' -Description "MyAppVersion"
$manifestVersion = Read-RequiredMatch -Path $manifestPath -Pattern '<assemblyIdentity version="([\d.]+)" name="LatencyBench\.App"' -Description "assemblyIdentity version"

# app.manifest's assemblyIdentity version is a 4-part Win32 manifest version, matching the
# AssemblyVersion Directory.Build.props derives as "$(LatencyBenchVersion).0".
$expectedManifestVersion = "$propsVersion.0"

Write-Host "Directory.Build.props LatencyBenchVersion : $propsVersion"
Write-Host "installer/LatencyBench.iss MyAppVersion    : $issVersion"
Write-Host "app.manifest assemblyIdentity version      : $manifestVersion (expected $expectedManifestVersion)"

$failures = @()

if ($issVersion -ne $propsVersion) {
    $failures += "installer/LatencyBench.iss MyAppVersion ('$issVersion') does not match Directory.Build.props LatencyBenchVersion ('$propsVersion')."
}

if ($manifestVersion -ne $expectedManifestVersion) {
    $failures += "app.manifest assemblyIdentity version ('$manifestVersion') does not match the expected '$expectedManifestVersion' derived from Directory.Build.props LatencyBenchVersion ('$propsVersion')."
}

if ($failures.Count -gt 0) {
    Write-Host "::error::Version drift detected between Directory.Build.props, installer/LatencyBench.iss, and app.manifest:"
    foreach ($failure in $failures) {
        Write-Host "::error::$failure"
    }
    Write-Host ""
    Write-Host "Fix: update installer/LatencyBench.iss and/or src/LatencyBench.App/app.manifest to match Directory.Build.props (LatencyBenchVersion = $propsVersion), or update Directory.Build.props if that one is actually the stale one."
    exit 1
}

Write-Host "Version is in sync across Directory.Build.props, installer/LatencyBench.iss, and app.manifest."
exit 0
