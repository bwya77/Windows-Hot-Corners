#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls Hot Corners for Windows.

.DESCRIPTION
    Stops the running process, removes the install directory, and removes the
    Run-at-login registry value. User settings at %AppData%\HotCorners are
    preserved unless -PurgeSettings is specified.

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

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\HotCorners'
$settingsDir = Join-Path $env:APPDATA 'HotCorners'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Write-Host ""
Write-Host "Uninstalling Hot Corners..." -ForegroundColor Cyan

Get-Process -Name HotCorners -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Milliseconds 500

if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
    Write-Host "Removed $installDir"
}

if ((Get-ItemProperty -Path $runKey -Name 'HotCorners' -ErrorAction SilentlyContinue)) {
    Remove-ItemProperty -Path $runKey -Name 'HotCorners' -ErrorAction SilentlyContinue
    Write-Host "Removed autostart registry value"
}

if ($PurgeSettings -and (Test-Path $settingsDir)) {
    Remove-Item $settingsDir -Recurse -Force
    Write-Host "Removed settings at $settingsDir"
} elseif (Test-Path $settingsDir) {
    Write-Host "Settings preserved at $settingsDir (re-run with -PurgeSettings to remove)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Hot Corners uninstalled." -ForegroundColor Green
Write-Host ""
