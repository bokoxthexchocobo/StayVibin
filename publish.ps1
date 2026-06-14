# Builds a self-contained, single-file Windows executable for StayVibin.
# Output: bin\Release\net10.0-windows\win-x64\publish\StayVibin.exe
#
# Self-contained means the .NET runtime is bundled, so the .exe runs on a
# machine without .NET installed. It does NOT bundle the local AI engine; the app
# installs and launches agent-server.exe from the provider package on first run.

param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    dotnet publish -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true

    $out = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\$Runtime\publish\StayVibin.exe"
    if (Test-Path $out) {
        Write-Host ""
        Write-Host "Built: $out"
    } else {
        Write-Warning "Publish finished but exe not found at expected path."
    }
}
finally {
    Pop-Location
}
