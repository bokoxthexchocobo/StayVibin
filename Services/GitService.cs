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

    /// <summary>
    /// Map every changed path in the repo to its git status code (the worktree
    /// column from 'git status --porcelain', or '?' for untracked). Keys are
    /// absolute paths. Empty if the folder isn't a repo or git is unavailable.
    /// Used to color the explorer tree.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, char>> GetStatusMapAsync(string workingDir)
    {
        var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        if (!GitAvailable || string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            return map;

        var (topOk, topOut) = await RunAsync("git", "rev-parse --show-toplevel", workingDir);
        if (!topOk) return map;
        var root = topOut.Trim();
        if (string.IsNullOrEmpty(root)) return map;

        var (ok, output) = await RunAsync("git", "status --porcelain", workingDir);
        if (!ok) return map;

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4) continue;

            char x = line[0], y = line[1];
            // Untracked wins; otherwise prefer the worktree column, then the index.
            char code = (x == '?' || y == '?') ? '?' : (y != ' ' ? y : x);

            var pathPart = line[3..];
            var arrow = pathPart.IndexOf(" -> ", StringComparison.Ordinal);  // renames
            if (arrow >= 0) pathPart = pathPart[(arrow + 4)..];
            pathPart = pathPart.Trim().Trim('"');
            if (pathPart.Length == 0) continue;

            try
            {
                var full = Path.GetFullPath(Path.Combine(root,
                    pathPart.Replace('/', Path.DirectorySeparatorChar)));
                map[full] = code;
            }
            catch { /* skip unparseable paths */ }
        }
        return map;
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

    /// <summary>
    /// Sign out of GitHub (github.com) via gh. Runs headlessly; returns whether it
    /// succeeded plus any message (gh's error text on failure, e.g. when multiple
    /// accounts require an explicit --user).
    /// </summary>
    public static async Task<(bool ok, string message)> GhLogoutAsync()
    {
        if (!GhAvailable) return (false, "GitHub CLI (gh) is not installed.");
        var (ok, output) = await RunAsync("gh", "auth logout --hostname github.com", null);
        return (ok, output.Trim());
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
