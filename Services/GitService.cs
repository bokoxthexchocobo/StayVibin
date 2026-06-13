using System.Diagnostics;
using System.IO;

namespace StayVibin.Services;

/// <summary>Snapshot of a working directory's git state for the top-bar badge.</summary>
public sealed record GitStatus(string Branch, int Dirty, string? RepoSlug)
{
    public bool IsDirty => Dirty > 0;
}

/// <summary>
/// Thin wrapper over the local git and GitHub (gh) CLIs. It does not perform git
/// operations itself - the agent does that through its terminal tool - it only
/// detects availability/auth and reads repo status to surface in the UI.
/// </summary>
public static class GitService
{
    private static bool? _git;
    private static bool? _gh;

    public static bool GitAvailable => _git ??= Probe("git", "--version");
    public static bool GhAvailable => _gh ??= Probe("gh", "--version");

    /// <summary>
    /// Read branch, dirty-file count and GitHub slug for a folder, or null if the
    /// folder is not a usable repo. Git error text is never returned to callers.
    /// </summary>
    public static async Task<GitStatus?> GetStatusAsync(string workingDir)
    {
        if (!GitAvailable || string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            return null;

        var (inside, insideOut) = await RunAsync("git", "rev-parse --is-inside-work-tree", workingDir);
        if (!inside || !insideOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return null;

        var (branchOk, branchOut) = await RunAsync("git", "branch --show-current", workingDir);
        if (!branchOk) return null;
        var branch = branchOut.Trim();
        if (string.IsNullOrEmpty(branch))
        {
            var (shaOk, shaOut) = await RunAsync("git", "rev-parse --short HEAD", workingDir);
            branch = shaOk && !string.IsNullOrWhiteSpace(shaOut) ? shaOut.Trim() : "detached";
        }

        var (statusOk, statusOut) = await RunAsync("git", "status --porcelain", workingDir);
        if (!statusOk) return null;
        var dirty = statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        var (hasRemote, remoteOut) = await RunAsync("git", "remote get-url origin", workingDir);
        var slug = hasRemote ? ParseSlug(remoteOut.Trim()) : null;

        return new GitStatus(branch, dirty, slug);
    }

    /// <summary>The authenticated GitHub account (via gh), or null if gh is missing/unauthenticated.</summary>
    public static async Task<string?> GhAccountAsync()
    {
        if (!GhAvailable) return null;
        // gh writes auth status to stderr; RunAsync merges it in.
        var (_, output) = await RunAsync("gh", "auth status", null);
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            var idx = t.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rest = t[(idx + "account ".Length)..].Trim();
                var name = rest.Split(' ')[0];
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return null;
    }

    /// <summary>Turn an origin URL into "owner/repo" for display, or null.</summary>
    private static string? ParseSlug(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];

        // git@github.com:owner/repo  or  https://github.com/owner/repo
        var at = u.IndexOf('@');
        if (at >= 0)
        {
            var colon = u.IndexOf(':', at);
            if (colon >= 0) return u[(colon + 1)..];
        }
        var slashSlash = u.IndexOf("//", StringComparison.Ordinal);
        if (slashSlash >= 0)
        {
            var path = u[(slashSlash + 2)..];
            var firstSlash = path.IndexOf('/');
            if (firstSlash >= 0) return path[(firstSlash + 1)..];
        }
        return null;
    }

    private static bool Probe(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return true;
        }
        catch { return false; }
    }

    private static async Task<(bool ok, string output)> RunAsync(string tool, string args, string? workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;

            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return (false, ""); }
            // On success, use stdout only. On failure, keep stderr for callers
            // that intentionally parse command diagnostics (currently gh auth).
            return (p.ExitCode == 0, p.ExitCode == 0 ? stdout : stderr);
        }
        catch
        {
            return (false, "");
        }
    }
}
