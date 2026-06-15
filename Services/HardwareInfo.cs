using System.Diagnostics;
using System.Globalization;

namespace StayVibin.Services;

/// <summary>
/// Best-effort detection of the machine's accelerator memory (VRAM) and system RAM.
/// Used by the auto-tuner to fit a model's runtime context window to the memory that
/// is actually available. Vendor-neutral: NVIDIA is read via nvidia-smi; any GPU
/// (including AMD) falls back to the Windows display-driver registry. Every probe is
/// best-effort and cached for the app lifetime (hardware does not change at runtime),
/// and never throws - callers get 0 when a value cannot be determined.
/// </summary>
public static class HardwareInfo
{
    // -1 = not probed yet; 0 = probed but unknown; >0 = cached byte count.
    private static long _vramBytes = -1;
    private static readonly object _lock = new();

    /// <summary>
    /// Total dedicated VRAM of the largest discrete GPU, in bytes (0 when unknown).
    /// Uses TOTAL rather than free VRAM on purpose: at session start a previously
    /// loaded model is swapped out (the engine pins a single model via
    /// OLLAMA_MAX_LOADED_MODELS=1), so instantaneous free VRAM would wildly
    /// under-report the budget the next model actually gets.
    /// </summary>
    public static long GetVramBytes()
    {
        lock (_lock)
        {
            if (_vramBytes >= 0) return _vramBytes;
            var bytes = ProbeNvidiaSmi();
            if (bytes <= 0) bytes = ProbeRegistryVram();
            _vramBytes = bytes > 0 ? bytes : 0;
            return _vramBytes;
        }
    }

    /// <summary>
    /// Total physical system RAM in bytes (the budget in CPU mode, and the fallback
    /// when no GPU VRAM can be read). Returns 0 when unknown.
    /// </summary>
    public static long GetSystemRamBytes()
    {
        try
        {
            // On desktop Windows this reports installed physical memory (or the
            // container limit), which is a good proxy for total RAM and needs no
            // P/Invoke or extra package.
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0) return info.TotalAvailableMemoryBytes;
        }
        catch { /* fall through to unknown */ }
        return 0;
    }

    /// <summary>
    /// Short human-readable budget for the active compute device, e.g. "24.0 GB VRAM"
    /// or "31.1 GB RAM". Returns an empty string when the relevant memory is unknown.
    /// Shown in the auto-tune status line so users see what the fit was based on.
    /// </summary>
    public static string DescribeBudget(ComputeDevice device)
    {
        if (device == ComputeDevice.Cpu)
        {
            long ram = GetSystemRamBytes();
            return ram > 0 ? $"{ram / 1024d / 1024d / 1024d:0.0} GB RAM" : "";
        }

        // GPU mode budgets against VRAM first and borrows system RAM when VRAM alone
        // is tight, so report both pools when known (that is "what you have").
        long vram = GetVramBytes();
        long sysRam = GetSystemRamBytes();
        if (vram > 0 && sysRam > 0)
            return $"{vram / 1024d / 1024d / 1024d:0.0} GB VRAM + {sysRam / 1024d / 1024d / 1024d:0.0} GB RAM";
        if (vram > 0) return $"{vram / 1024d / 1024d / 1024d:0.0} GB VRAM";
        return sysRam > 0 ? $"{sysRam / 1024d / 1024d / 1024d:0.0} GB RAM" : "";
    }

    /// <summary>
    /// Query NVIDIA's total VRAM via nvidia-smi (max across GPUs, in bytes). Returns
    /// 0 when nvidia-smi is absent (no NVIDIA driver) or the call fails/times out.
    /// </summary>
    private static long ProbeNvidiaSmi()
    {
        try
        {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    // Do NOT redirect stderr: we never read it, and an undrained
                    // redirected stderr pipe can deadlock the child if it fills while
                    // we block on stdout. Unredirected stderr is discarded harmlessly
                    // for this windowless GUI process.
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            using var p = Process.Start(psi);
            if (p is null) return 0;

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return 0;
            }

            long maxMiB = 0;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib))
                    maxMiB = Math.Max(maxMiB, mib);
            }
            return maxMiB * 1024L * 1024L;
        }
        catch
        {
            return 0;   // nvidia-smi not found / not executable
        }
    }

    /// <summary>
    /// Universal VRAM fallback (covers AMD as well as NVIDIA when nvidia-smi is
    /// absent): the display driver records each GPU's dedicated VRAM under its class
    /// key as HardwareInformation.qwMemorySize. We take the largest so a tiny
    /// integrated GPU never wins over a discrete card. Read through PowerShell to stay
    /// free of a Windows-only registry package on this cross-target project.
    /// </summary>
    private static long ProbeRegistryVram()
    {
        try
        {
            const string script =
                "$m=0; Get-ItemProperty "
                + "'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}\\*' "
                + "-ErrorAction SilentlyContinue | ForEach-Object { "
                + "$v=$_.'HardwareInformation.qwMemorySize'; if ($v -gt $m) { $m=$v } }; $m";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    // See ProbeNvidiaSmi: leaving stderr unredirected avoids a
                    // pipe-fill deadlock since we only ever read stdout.
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            using var p = Process.Start(psi);
            if (p is null) return 0;

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return 0;
            }

            return long.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
                   && bytes > 0
                ? bytes
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
