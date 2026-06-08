#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Hot Corners for Windows.

.DESCRIPTION
    Downloads the latest release of Hot Corners from GitHub, places it in
    %LocalAppData%\Programs\HotCorners\, registers it to run at sign-in, and
    launches it.

.PARAMETER NoStart
    Install and register autostart, but don't launch immediately.

.PARAMETER NoAutostart
    Install but skip the Run-at-login registry entry.

.EXAMPLE
    irm https://raw.githubusercontent.com/bwya77/Windows-Hot-Corners/main/install.ps1 | iex

.EXAMPLE
    .\install.ps1 -NoAutostart
#>
[CmdletBinding()]
param(
    [switch]$NoStart,
    [switch]$NoAutostart
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo       = 'bwya77/Windows-Hot-Corners'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\HotCorners'
$exePath    = Join-Path $installDir 'HotCorners.exe'

# --- Architecture detection ------------------------------------------------
$archEnv = $env:PROCESSOR_ARCHITECTURE
if ($archEnv -eq 'ARM64') {
    $arch = 'arm64'
} elseif ([Environment]::Is64BitOperatingSystem) {
    $arch = 'x64'
} else {
    throw "Hot Corners requires 64-bit Windows. Detected: $archEnv"
}
$assetName = "HotCorners-$arch.exe"

Write-Host ""
Write-Host "  Hot Corners for Windows" -ForegroundColor Cyan
Write-Host "  -----------------------"
Write-Host "  Architecture : $arch"
Write-Host "  Install to   : $installDir"
Write-Host ""

# --- Stop existing instance ------------------------------------------------
$running = Get-Process -Name HotCorners -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running Hot Corners instance..." -ForegroundColor Yellow
    $running | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Milliseconds 600
}

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

$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) {
    $available = ($release.assets | ForEach-Object { $_.name }) -join ', '
    throw "Release $($release.tag_name) doesn't contain $assetName. Available: $available"
}

# --- Download -------------------------------------------------------------
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Write-Host "Downloading $($release.tag_name) ($([math]::Round($asset.size / 1MB, 1)) MB)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $exePath -UseBasicParsing

# Quick sanity: file exists and looks like a PE
if (-not (Test-Path $exePath) -or (Get-Item $exePath).Length -lt 100KB) {
    throw "Download failed or file is too small."
}

# --- Register autostart ---------------------------------------------------
if (-not $NoAutostart) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }
    Set-ItemProperty -Path $runKey -Name 'HotCorners' -Value "`"$exePath`""
    Write-Host "Registered Hot Corners to run at sign-in." -ForegroundColor Green
}

# --- Launch ---------------------------------------------------------------
if (-not $NoStart) {
    Start-Process -FilePath $exePath
    Start-Sleep -Seconds 1
    if (Get-Process -Name HotCorners -ErrorAction SilentlyContinue) {
        Write-Host "Hot Corners is running. Look for the tray icon to configure." -ForegroundColor Green
    } else {
        Write-Warning "Hot Corners process not detected after launch. Try running $exePath manually."
    }
}

Write-Host ""
Write-Host "Done. Installed to: $exePath" -ForegroundColor Cyan
Write-Host ""
