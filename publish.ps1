# Builds a self-contained, single-file Windows executable for StayVibin.
# Output: bin\Release\net10.0\win-x64\publish\StayVibin.exe
# plus an Engine\ folder when a bundled StayVibin Engine payload is available.
#
# Self-contained means the .NET runtime is bundled, so the .exe runs on a
# machine without .NET installed.

param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    function Resolve-EngineSource {
        # Each candidate carries BOTH the engine exe and its lib\ollama runtime dir,
        # because the layout differs by source: the project/override copies keep the
        # runtime under "lib\ollama", while the dev ollama-fork build keeps it under
        # "build\lib\ollama". Returning both avoids the old bug where the copy always
        # assumed the dev "build\lib\ollama" path and failed for the project source.
        $candidates = @()

        $projectEngine = Join-Path $PSScriptRoot "Engine"
        $candidates += [pscustomobject]@{
            Exe = (Join-Path $projectEngine "stayvibin-engine.exe")
            Lib = (Join-Path $projectEngine "lib\ollama")
        }

        if ($env:STAYVIBIN_ENGINE_SOURCE) {
            $candidates += [pscustomobject]@{
                Exe = (Join-Path $env:STAYVIBIN_ENGINE_SOURCE "stayvibin-engine.exe")
                Lib = (Join-Path $env:STAYVIBIN_ENGINE_SOURCE "lib\ollama")
            }
        }

        $devRoot = Join-Path $env:USERPROFILE "ollama-fork"
        $candidates += [pscustomobject]@{
            Exe = (Join-Path $devRoot "stayvibin-engine.exe")
            Lib = (Join-Path $devRoot "build\lib\ollama")
        }

        foreach ($c in $candidates) {
            if ((Test-Path $c.Exe) -and (Test-Path $c.Lib)) { return $c }
        }
        return $null
    }

    dotnet publish .\StayVibin.csproj -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true

    # Resolve the publish output robustly instead of hardcoding the target
    # framework moniker (it changed from net10.0-windows to net10.0 during the
    # Avalonia migration). Search under bin\Release for the published exe.
    $out = Get-ChildItem -Path (Join-Path $PSScriptRoot "bin\Release") -Recurse -Filter "StayVibin.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\$Runtime\publish\StayVibin.exe" } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $out) {
        $out = Join-Path $PSScriptRoot "bin\Release\net10.0\$Runtime\publish\StayVibin.exe"
    }
    $publishDir = Split-Path $out -Parent
    $engineDest = Join-Path $publishDir "Engine"

    # The csproj already copies Engine\** into the publish output (Content with
    # CopyToPublishDirectory). When that produced a complete payload there is nothing
    # to do; the manual copy below is only the fallback for when the project has no
    # local Engine\ (e.g. a clean checkout building from the dev ollama-fork).
    $alreadyBundled = (Test-Path (Join-Path $engineDest "stayvibin-engine.exe")) `
        -and (Test-Path (Join-Path $engineDest "lib\ollama"))

    if ($alreadyBundled) {
        Write-Host "Engine already present in publish output (bundled via project Content)."
    }
    else {
        $engineSource = Resolve-EngineSource
        if ($engineSource) {
            if (Test-Path $engineDest) {
                Remove-Item $engineDest -Recurse -Force
            }
            New-Item -ItemType Directory -Force -Path $engineDest | Out-Null
            Copy-Item $engineSource.Exe (Join-Path $engineDest "stayvibin-engine.exe") -Force
            Copy-Item $engineSource.Lib (Join-Path $engineDest "lib\ollama") -Recurse -Force
            Write-Host "Bundled engine exe: $($engineSource.Exe)"
        }
        else {
            Write-Warning "Bundled StayVibin Engine payload was not found. Publish output will not contain Engine\."
        }
    }

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
