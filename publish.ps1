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

    # The Go+CGO engine exe links a handful of MinGW runtime DLLs that are NOT part
    # of a stock Windows install (the loader resolves them from the exe's own
    # directory). They are easy to forget because they live in the toolchain bin,
    # not in the engine's build output - which is exactly the bug that shipped an
    # Engine\ payload missing libwinpthread-1.dll. Stage every required DLL right
    # next to the engine exe so a clean Windows box can launch it.
    function Stage-EngineRuntimeDlls {
        param([Parameter(Mandatory)][string]$EngineDir)

        # Currently the only non-Windows load-time import of stayvibin-engine.exe.
        # Add more here if a future engine build pulls in additional MinGW deps
        # (e.g. libstdc++-6.dll, libgcc_s_seh-1.dll); the search below will find them.
        $required = @("libwinpthread-1.dll")

        # Prioritized sources: a checked-in copy guarantees a reproducible bundle on
        # any build machine, then fall back to common MinGW/MSYS2/Git toolchain bins.
        $sourceDirs = @(
            (Join-Path $PSScriptRoot "runtime-deps\win-x64"),
            $EngineDir,
            (Join-Path $env:USERPROFILE "mingw64\bin"),
            (Join-Path $env:USERPROFILE "ucrt64\bin"),
            "C:\msys64\mingw64\bin",
            "C:\msys64\ucrt64\bin",
            "C:\Program Files\Git\mingw64\bin"
        )

        foreach ($dll in $required) {
            $dest = Join-Path $EngineDir $dll
            if (Test-Path $dest) { continue }
            $found = $false
            foreach ($dir in $sourceDirs) {
                $src = Join-Path $dir $dll
                if (Test-Path $src) {
                    Copy-Item $src $dest -Force
                    Write-Host "Staged engine runtime DLL: $dll  (from $dir)"
                    $found = $true
                    break
                }
            }
            if (-not $found) {
                Write-Warning "Required engine runtime DLL '$dll' was not found in any known source. The published engine may fail to start. Add it to runtime-deps\win-x64\."
            }
        }
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

    # Always ensure the engine's MinGW runtime DLLs are present next to the exe,
    # whether the payload came from project Content or the fallback copy above.
    if (Test-Path (Join-Path $engineDest "stayvibin-engine.exe")) {
        Stage-EngineRuntimeDlls -EngineDir $engineDest
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
