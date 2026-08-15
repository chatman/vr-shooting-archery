<#
    Builds the APK and sideloads it onto a tethered Quest 3.

    Usage:
        .\deploy.ps1              # build, install, and launch
        .\deploy.ps1 -SkipBuild   # reinstall the last APK without rebuilding
        .\deploy.ps1 -NoLaunch    # install but do not start the app
#>
param(
    [switch]$SkipBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$ProjectPath = $PSScriptRoot
$ApkPath     = Join-Path $ProjectPath 'Builds\vr-shooting-archery.apk'
$PackageName = 'com.ishanxr.vrshootingarchery'
$LogPath     = Join-Path $ProjectPath 'Builds\build.log'

# The editor version the project is pinned to. Kept in step with
# ProjectSettings/ProjectVersion.txt, which Unity rewrites on upgrade.
$EditorVersion = (Select-String -Path (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt') `
                    -Pattern '^m_EditorVersion:\s*(.+)$').Matches[0].Groups[1].Value.Trim()

# Look where Unity Hub actually installs, rather than hardcoding one machine's
# layout. $env:UNITY_EDITOR wins if set, for anything unusual.
$candidates = @(
    $env:UNITY_EDITOR,
    "D:\Unity\Editors\$EditorVersion\Editor\Unity.exe",
    "C:\Program Files\Unity\Hub\Editor\$EditorVersion\Editor\Unity.exe",
    "$env:LOCALAPPDATA\Unity\Hub\Editor\$EditorVersion\Editor\Unity.exe"
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $candidates) {
    throw ("Unity $EditorVersion not found. Install it from Unity Hub, or point " +
           '$env:UNITY_EDITOR at the Unity.exe you want to build with.')
}
$Unity = $candidates[0]
Write-Host "==> Using Unity $EditorVersion at $Unity" -ForegroundColor DarkGray

if (-not $SkipBuild) {
    New-Item -ItemType Directory -Force (Split-Path $ApkPath) | Out-Null

    Write-Host '==> Building APK (this takes a few minutes on a cold IL2CPP build)...' -ForegroundColor Cyan
    $proc = Start-Process $Unity -Wait -PassThru -ArgumentList @(
        '-batchmode', '-quit', '-nographics',
        '-projectPath', $ProjectPath,
        '-buildTarget', 'Android',
        '-executeMethod', 'QuestBuild.BuildApk',
        '-outputPath', $ApkPath,
        '-logFile', $LogPath
    )

    if ($proc.ExitCode -ne 0) {
        Write-Host "==> Build FAILED (exit $($proc.ExitCode)). Last lines of the log:" -ForegroundColor Red
        Get-Content $LogPath -Tail 40
        throw "Build failed. Full log: $LogPath"
    }
    Write-Host '==> Build succeeded.' -ForegroundColor Green
}

if (-not (Test-Path $ApkPath)) {
    throw "No APK at $ApkPath. Run without -SkipBuild first."
}

Write-Host '==> Checking for a connected headset...' -ForegroundColor Cyan
$devices = (adb devices) -split "`n" | Where-Object { $_ -match '\tdevice$' }
if (-not $devices) {
    Write-Host 'No device found. Check that:' -ForegroundColor Yellow
    Write-Host '  1. The Quest is plugged in over USB-C.'
    Write-Host '  2. Developer Mode is on (Meta Horizon phone app > Devices > Headset Settings).'
    Write-Host '  3. You accepted the "Allow USB debugging" prompt inside the headset.'
    throw 'No adb device.'
}
Write-Host "==> Found: $($devices -join ', ')" -ForegroundColor Green

Write-Host '==> Installing...' -ForegroundColor Cyan
adb install -r $ApkPath

if (-not $NoLaunch) {
    Write-Host '==> Launching on headset...' -ForegroundColor Cyan
    adb shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null
    Write-Host '==> Running. Put the headset on.' -ForegroundColor Green
}
