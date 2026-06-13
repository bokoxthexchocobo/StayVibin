# Builds a self-contained StayVibin.exe and packages it into a Windows setup
# installer (Inno Setup). Output: dist\StayVibin-<version>-setup.exe
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 1.0.0

param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

function Get-ProjectVersion {
    param([string]$CsprojPath)
    $text = Get-Content -Raw -Path $CsprojPath
    if ($text -match '<Version>([^<]+)</Version>') { return $Matches[1].Trim() }
    throw "Could not read <Version> from $CsprojPath"
}

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path $Root "tools\inno\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

function Ensure-InnoCompiler {
    $existing = Find-InnoCompiler
    if ($existing) { return $existing }

    Write-Host "Inno Setup 6 not found. Downloading installer..."
    $toolsDir = Join-Path $Root "tools\inno"
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

    $installer = Join-Path $env:TEMP "innosetup-6.exe"
    Invoke-WebRequest -Uri "https://jrsoftware.org/download.php/is.exe" -OutFile $installer

    Write-Host "Installing Inno Setup 6 (silent)..."
    $proc = Start-Process -FilePath $installer -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"
    ) -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "Inno Setup installer exited with code $($proc.ExitCode)"
    }

    $iscc = Find-InnoCompiler
    if (-not $iscc) {
        throw "Inno Setup was installed but ISCC.exe was not found. Install Inno Setup 6 manually from https://jrsoftware.org/isinfo.php"
    }
    return $iscc
}

Push-Location $Root
try {
    $csproj = Join-Path $Root "OpenHandsDesktop.csproj"
    if (-not $Version) { $Version = Get-ProjectVersion $csproj }
    Write-Host "Building StayVibin v$Version"

    if (-not $SkipPublish) {
        Write-Host ""
        Write-Host "==> Publishing self-contained executable..."
        & (Join-Path $Root "publish.ps1") -Runtime $Runtime
    }

    $publishedExe = Join-Path $Root "bin\Release\net10.0-windows\$Runtime\publish\StayVibin.exe"
    if (-not (Test-Path $publishedExe)) {
        throw "Published executable not found: $publishedExe`nRun publish.ps1 first or omit -SkipPublish."
    }

    $distDir = Join-Path $Root "dist"
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null

    Write-Host ""
    Write-Host "==> Compiling Windows installer..."
    $iscc = Ensure-InnoCompiler
    $iss = Join-Path $Root "installer\StayVibin.iss"
    & $iscc "/DMyAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

    $setup = Join-Path $distDir "StayVibin-$Version-setup.exe"
    if (-not (Test-Path $setup)) {
        throw "Installer was not created at expected path: $setup"
    }

    $sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Built installer: $setup ($sizeMb MB)"
    Write-Host "Portable exe:    $publishedExe"
}
finally {
    Pop-Location
}
