# VoxLeap P0-01 environment setup for the Windows side.
# Prepares the minimal toolchain required by docs/05 (Phase 0 / WinUI 3 mockup):
#   .NET 8 SDK via winget. Windows App SDK / Windows SDK arrive through NuGet
#   (Microsoft.WindowsAppSDK + Microsoft.Windows.SDK.BuildTools) at build time,
#   so no Visual Studio installation is strictly required for a CLI build.
#
# Keep this file ASCII-only: Windows PowerShell 5.1 reads UTF-8 scripts without
# BOM incorrectly, which can garble non-ASCII comments and strings.
#
# Usage (PowerShell, admin recommended for machine-wide SDK install):
#   powershell -ExecutionPolicy Bypass -File setup-windows-dev.ps1

[CmdletBinding()]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

function Write-Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Write-Ok($message)   { Write-Host "    [OK] $message" -ForegroundColor Green }
function Write-Warn2($message){ Write-Host "    [!]  $message" -ForegroundColor Yellow }

Write-Step "Checking admin rights"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Write-Ok "Running elevated"
} else {
    Write-Warn2 "Not elevated. winget may trigger a UAC prompt for the SDK install."
}

Write-Step "Checking winget availability"
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Warn2 "winget not found. Install 'App Installer' from Microsoft Store, then re-run."
    exit 1
}
$wingetVersion = (winget --version) 2>$null
Write-Ok "winget $wingetVersion"

Write-Step "Checking existing .NET SDKs"
$sdks = @()
try { $sdks = @(dotnet --list-sdks 2>$null) } catch { $sdks = @() }
if ($sdks.Count -gt 0) { $sdks | ForEach-Object { Write-Host "    $_" } }
$hasNet8 = $sdks | Where-Object { $_ -match "^8\." }
if ($hasNet8) {
    Write-Ok ".NET 8 SDK already installed; skipping install"
} elseif ($SkipInstall) {
    Write-Warn2 "-SkipInstall given and .NET 8 SDK missing. Nothing installed."
    exit 1
} else {
    Write-Host "    Installing Microsoft.DotNet.SDK.8 via winget..."
    winget install --id Microsoft.DotNet.SDK.8 `
        --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Warn2 "winget install failed (exit $LASTEXITCODE). Try: winget upgrade Microsoft.DotNet.SDK.8"
        exit 1
    }
    Write-Ok "winget install finished"
}

Write-Step "Verification (matches docs/05)"
# A fresh PATH may be needed in this session after a fresh install.
$dotnetDirs = @(
    "$env:ProgramFiles\dotnet",
    "${env:ProgramFiles(x86)}\dotnet"
) | Where-Object { Test-Path (Join-Path $_ "dotnet.exe") }
$dotnetExe = if ($dotnetDirs.Count -gt 0) { Join-Path $dotnetDirs[0] "dotnet.exe" } else { "dotnet" }

Write-Host "    & '$dotnetExe' --info"
& $dotnetExe --info
if ($LASTEXITCODE -ne 0) { Write-Warn2 "dotnet --info failed. Open a NEW terminal and re-run verification."; exit 1 }

Write-Host "    & '$dotnetExe' --list-sdks"
& $dotnetExe --list-sdks

Write-Host "    where.exe dotnet"
where.exe dotnet

Write-Step "Result"
$finalSdks = @()
try { $finalSdks = @(& $dotnetExe --list-sdks 2>$null) } catch { $finalSdks = @() }
if ($finalSdks | Where-Object { $_ -match "^8\." }) {
    Write-Ok ".NET 8 SDK is ready. Next: create src/VoxLeap.App per docs/05."
    Write-Host "    Evidence required by docs/05: build log + real Windows screenshots"
    Write-Host "    (tray, hotkey overlay, no-activation focus behavior)."
} else {
    Write-Warn2 ".NET 8 SDK still not detected. Reboot may be required, or install manually:"
    Write-Host "    https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}
