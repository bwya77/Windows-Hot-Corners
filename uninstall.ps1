#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls Hot Corners for Windows.

.DESCRIPTION
    Runs the installer's silent uninstaller (registered in Apps & Features).
    Also cleans up any legacy %LocalAppData%\Programs\HotCorners install left
    over from older script-based installations. User settings at
    %AppData%\HotCorners are preserved unless -PurgeSettings is specified.

.PARAMETER PurgeSettings
    Also delete user settings at %AppData%\HotCorners.

.EXAMPLE
    irm https://raw.githubusercontent.com/bwya77/Windows-Hot-Corners/main/uninstall.ps1 | iex
#>
[CmdletBinding()]
param(
    [switch]$PurgeSettings
)

$ErrorActionPreference = 'Stop'

$settingsDir = Join-Path $env:APPDATA 'HotCorners'

Write-Host ""
Write-Host "Uninstalling Hot Corners..." -ForegroundColor Cyan

# --- Stop running app -----------------------------------------------------
Get-Process -Name HotCorners -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Milliseconds 400

# --- Run Inno Setup uninstaller (Apps & Features entry) -------------------
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4A1D8E7B-6F94-4F2E-A1E1-3D6B9C2A1A6E}_is1'
$uninstaller = (Get-ItemProperty -Path $uninstallKey -Name 'QuietUninstallString' -ErrorAction SilentlyContinue).QuietUninstallString
if (-not $uninstaller) {
    $uninstaller = (Get-ItemProperty -Path $uninstallKey -Name 'UninstallString' -ErrorAction SilentlyContinue).UninstallString
    if ($uninstaller) { $uninstaller += ' /SILENT' }
}

if ($uninstaller) {
    Write-Host "Running registered uninstaller (you'll see a UAC prompt)..." -ForegroundColor Yellow
    # The registered string already includes the full path + flags. Use cmd to honor it verbatim.
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $uninstaller -Verb RunAs -Wait
} else {
    Write-Host "No Inno Setup uninstaller registered." -ForegroundColor Yellow
}

# --- Clean up legacy script install ---------------------------------------
$legacyDir = Join-Path $env:LOCALAPPDATA 'Programs\HotCorners'
if (Test-Path $legacyDir) {
    Remove-Item $legacyDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed legacy install at $legacyDir"
}

# Belt and suspenders — the installer's CurUninstallStepChanged also does this.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ((Get-ItemProperty -Path $runKey -Name 'HotCorners' -ErrorAction SilentlyContinue)) {
    Remove-ItemProperty -Path $runKey -Name 'HotCorners' -ErrorAction SilentlyContinue
}

# --- Settings -------------------------------------------------------------
if ($PurgeSettings -and (Test-Path $settingsDir)) {
    Remove-Item $settingsDir -Recurse -Force
    Write-Host "Removed settings at $settingsDir"
} elseif (Test-Path $settingsDir) {
    Write-Host "Settings preserved at $settingsDir (re-run with -PurgeSettings to remove)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Hot Corners uninstalled." -ForegroundColor Green
Write-Host ""
