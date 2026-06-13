using System.IO;
using System.Text;

namespace StayVibin.Services;

/// <summary>
/// Builds a compact, Cursor-style workspace snapshot injected into the agent's
/// system prompt at session start so the model knows the repo layout, git state,
/// and project type before it guesses.
/// </summary>
public static class WorkspaceContextService
{
    private const int MaxDirtyFiles = 25;
    private const int MaxTopEntries = 40;

    /// <summary>
    /// Snapshot text appended ahead of the agentic rules. Returns empty if the
    /// folder is missing or unreadable.
    /// </summary>
    public static async Task<string> BuildAsync(string workingDir, string? editorPath = null)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("Workspace snapshot (ground truth for this session - re-read files with tools if unsure):");
        sb.AppendLine($"- Root: {workingDir}");

        var gitLine = await BuildGitLineAsync(workingDir);
        if (gitLine is not null) sb.AppendLine(gitLine);

        var toolingLine = await BuildToolingLineAsync();
        if (toolingLine is not null) sb.AppendLine(toolingLine);

        var project = DetectProject(workingDir);
        if (project is not null) sb.AppendLine($"- Project: {project}");

        var layout = BuildTopLevelLayout(workingDir);
        if (layout.Length > 0) sb.AppendLine($"- Top level: {layout}");

        if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
        {
            var rel = TryRelative(workingDir, editorPath);
            sb.AppendLine($"- Editor: user is viewing {rel}");
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string?> BuildGitLineAsync(string workingDir)
    {
        if (!GitService.GitAvailable) return null;

        var status = await GitService.GetStatusAsync(workingDir);
        if (status is null) return "- Git: (not a repository)";

        var map = await GitService.GetStatusMapAsync(workingDir);
        var relPaths = map.Keys
            .Select(p => TryRelative(workingDir, p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDirtyFiles)
            .ToList();

        var dirtyPart = relPaths.Count == 0
            ? "clean"
            : $"{map.Count} changed file(s): {string.Join(", ", relPaths)}"
              + (map.Count > MaxDirtyFiles ? ", ..." : "");

        var slug = status.RepoSlug is not null ? $" ({status.RepoSlug})" : "";
        return $"- Git: branch {status.Branch}{slug}, {dirtyPart}";
    }

    private static string BuildTopLevelLayout(string workingDir)
    {
        var parts = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories(workingDir)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (WorkspaceExplorer.Ignored.Contains(name)) continue;
                parts.Add(name + "/");
                if (parts.Count >= MaxTopEntries) break;
            }

            foreach (var f in Directory.GetFiles(workingDir)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                parts.Add(Path.GetFileName(f));
                if (parts.Count >= MaxTopEntries) break;
            }
        }
        catch { return ""; }

        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private static async Task<string?> BuildToolingLineAsync()
    {
        if (!GitService.GitAvailable)
            return "- Tooling: git not found on PATH";

        if (!GitService.GhAvailable)
            return "- Tooling: git ready; GitHub CLI (gh) not installed";

        var account = await GitService.GhAccountAsync();
        return account is null
            ? "- Tooling: git ready; gh installed but not signed in"
            : $"- Tooling: git ready; gh signed in as {account}";
    }

    private static string? DetectProject(string workingDir)
    {
        try
        {
            // Top-level only (and one subfolder) - avoid scanning huge trees at session start.
            if (Directory.GetFiles(workingDir, "*.sln").Length > 0
                || Directory.GetFiles(workingDir, "*.csproj").Length > 0
                || HasFileInImmediateSubdirs(workingDir, "*.csproj"))
                return ".NET (solution or csproj present)";

            if (File.Exists(Path.Combine(workingDir, "package.json")))
                return "Node.js (package.json)";

            if (File.Exists(Path.Combine(workingDir, "pyproject.toml"))
                || File.Exists(Path.Combine(workingDir, "requirements.txt")))
                return "Python";

            if (File.Exists(Path.Combine(workingDir, "Cargo.toml")))
                return "Rust (Cargo)";

            if (File.Exists(Path.Combine(workingDir, "go.mod")))
                return "Go module";

            if (File.Exists(Path.Combine(workingDir, "CMakeLists.txt"))
                || HasFileInImmediateSubdirs(workingDir, "CMakeLists.txt"))
                return "CMake/C++";
        }
        catch { /* best effort */ }

        return null;
    }

    /// <summary>True if any immediate child directory contains a file matching the pattern.</summary>
    private static bool HasFileInImmediateSubdirs(string root, string pattern)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (WorkspaceExplorer.Ignored.Contains(name)) continue;
            if (Directory.GetFiles(dir, pattern).Length > 0) return true;
        }
        return false;
    }

    private static string TryRelative(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (!rel.StartsWith("..")) return rel.Replace('\\', '/');
        }
        catch { }
        return path.Replace('\\', '/');
    }
}
