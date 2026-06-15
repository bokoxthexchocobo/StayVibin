using System.Diagnostics;
using System.IO;

namespace StayVibin.Services;

/// <summary>
/// One-click setup for the OpenHands agent-server so the user never has to open a
/// terminal. It locates (or installs) Astral's `uv` tool manager, then runs
/// `uv tool install openhands --python 3.12`, which places agent-server.exe under
/// %APPDATA%\uv\tools\openhands\. All command output is surfaced via
/// <see cref="LogLine"/> so the UI can mirror it into the server-log pane.
/// </summary>
public sealed class OpenHandsInstaller
{
    /// <summary>Raised for each line of installer stdout/stderr (mirror to the UI log).</summary>
    public event Action<string>? LogLine;

    /// <summary>
    /// Locate uv: first on PATH, then at the default per-user install location
    /// (%USERPROFILE%\.local\bin\uv.exe). Returns null if uv is not present.
    /// </summary>
    public static string? FindUv()
    {
        var onPath = ProbeOnPath("uv");
        if (onPath is not null) return onPath;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "uv.exe");
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// Ensure the OpenHands agent-server is installed. Installs uv first if needed.
    /// Returns true only when the uv tool install completes successfully.
    /// </summary>
    public async Task<bool> EnsureInstalledAsync(CancellationToken ct = default)
    {
        var uv = FindUv();
        if (uv is null)
        {
            Log("uv (Python tool manager) not found - installing it first...");
            if (!await InstallUvAsync(ct))
            {
                Log("Could not install uv automatically.");
                return false;
            }
            uv = FindUv();
            if (uv is null)
            {
                Log("uv was installed but could not be located afterwards.");
                return false;
            }
        }

        Log($"Using uv at {uv}");
        Log("Installing OpenHands (uv tool install openhands --python 3.12). This can take a few minutes...");
        var ok = await RunStreamingAsync(uv, "tool install openhands --python 3.12", ct);
        Log(ok ? "OpenHands install completed." : "OpenHands install failed (see output above).");
        return ok;
    }

    /// <summary>Install uv via Astral's official standalone PowerShell installer.</summary>
    private async Task<bool> InstallUvAsync(CancellationToken ct)
    {
        // Force TLS 1.2 first - some Windows 10 hosts still default to older
        // protocols, which makes the https download fail before it starts.
        const string script =
            "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; "
            + "irm https://astral.sh/uv/install.ps1 | iex";
        return await RunStreamingAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            ct);
    }

    /// <summary>Run a process, streaming its output to <see cref="LogLine"/>.</summary>
    private async Task<bool> RunStreamingAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log($"[installer] {ex.Message}");
            return false;
        }
    }

    /// <summary>Resolve a tool on PATH via where.exe; returns its full path or null.</summary>
    private static string? ProbeOnPath(string tool)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = tool,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return null; }
            if (p.ExitCode != 0) return null;

            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch
        {
            return null;
        }
    }

    private void Log(string line) => LogLine?.Invoke(line);
}
