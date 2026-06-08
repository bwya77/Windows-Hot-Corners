#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Hot Corners for Windows.

.DESCRIPTION
    Downloads the latest Hot Corners installer from GitHub and runs it with UAC
    elevation. The installer puts Hot Corners in "C:\Program Files\Hot Corners",
    registers a clean uninstaller in Apps & Features, optionally sets it to
    start at sign-in, and launches it.

.PARAMETER NoStartup
    Install without the "Start with Windows" task ticked. You can still toggle
    autostart later from the Hot Corners Settings window.

.PARAMETER NoStart
    Install but don't launch immediately. (Default: launch after install.)

.EXAMPLE
    irm https://raw.githubusercontent.com/bwya77/Windows-Hot-Corners/main/install.ps1 | iex

.EXAMPLE
    .\install.ps1 -NoStartup
#>
[CmdletBinding()]
param(
    [switch]$NoStartup,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'bwya77/Windows-Hot-Corners'

# --- Architecture detection ------------------------------------------------
$archEnv = $env:PROCESSOR_ARCHITECTURE
if ($archEnv -eq 'ARM64') {
    $arch = 'arm64'
} elseif ([Environment]::Is64BitOperatingSystem) {
    $arch = 'x64'
} else {
    throw "Hot Corners requires 64-bit Windows. Detected: $archEnv"
}

Write-Host ""
Write-Host "  Hot Corners for Windows" -ForegroundColor Cyan
Write-Host "  -----------------------"
Write-Host "  Architecture : $arch"
Write-Host ""

# --- Resolve latest release asset -----------------------------------------
Write-Host "Looking up latest release..."
try {
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repo/releases/latest" `
        -Headers @{ 'User-Agent' = 'HotCorners-Installer'; 'Accept' = 'application/vnd.github+json' } `
        -ErrorAction Stop
} catch {
    throw "Couldn't reach GitHub: $($_.Exception.Message)"
}

$asset = $release.assets | Where-Object {
    $_.name -like "HotCornersSetup-*-win-$arch.exe"
} | Select-Object -First 1

if (-not $asset) {
    $available = ($release.assets | ForEach-Object { $_.name }) -join ', '
    throw "Release $($release.tag_name) doesn't contain a Hot Corners installer for $arch. Available: $available"
}

# --- Download -------------------------------------------------------------
$tempPath = Join-Path $env:TEMP $asset.name
Write-Host "Downloading $($release.tag_name) ($([math]::Round($asset.size / 1MB, 1)) MB)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tempPath -UseBasicParsing

if (-not (Test-Path $tempPath) -or (Get-Item $tempPath).Length -lt 500KB) {
    throw "Download failed or file is too small."
}

# --- Run installer (UAC prompt) -------------------------------------------
# Pass /SILENT so installation runs without showing the wizard. The installer's
# "Start with Windows" task is ticked by default; use /TASKS=! to opt out.
$args = @('/SILENT')
if ($NoStartup) {
    $args += '/TASKS=!startupwithwindows'
}

Write-Host "Launching installer (you'll see a UAC prompt)..." -ForegroundColor Yellow
$proc = Start-Process -FilePath $tempPath -ArgumentList $args -Verb RunAs -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "Installer exited with code $($proc.ExitCode)."
}

Remove-Item $tempPath -Force -ErrorAction SilentlyContinue

# --- Launch (if not already started by installer) -------------------------
if (-not $NoStart) {
    Start-Sleep -Seconds 1
    if (-not (Get-Process -Name HotCorners -ErrorAction SilentlyContinue)) {
        $exePath = Join-Path $env:ProgramFiles 'Hot Corners\HotCorners.exe'
        if (Test-Path $exePath) {
            Start-Process -FilePath $exePath
        }
    }
}

Write-Host ""
Write-Host "Hot Corners installed. Look for the tray icon to configure." -ForegroundColor Green
Write-Host ""
