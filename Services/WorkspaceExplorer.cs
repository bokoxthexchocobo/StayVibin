using System.IO;
using StayVibin.Models;

namespace StayVibin.Services;

/// <summary>
/// Builds the explorer tree for a working directory: folders first, then files,
/// each alphabetical. Heavy/noise directories (.git, node_modules, build output)
/// are skipped, and each entry is tagged with its git status for coloring.
/// Directories are loaded one level at a time (lazy) to stay fast on big repos.
/// </summary>
public static class WorkspaceExplorer
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build",
        "__pycache__", ".venv", "venv", "packages", "target", ".next", ".nuget",
        ".gradle", ".pytest_cache", ".mypy_cache"
    };

    /// <summary>A single not-yet-loaded marker child so a folder shows its expander.</summary>
    public static FileNode Placeholder() => new() { IsPlaceholder = true, Name = "..." };

    /// <summary>
    /// List the immediate children of <paramref name="dir"/>. Each folder gets a
    /// placeholder child for lazy expansion. Status is read from the supplied map.
    /// </summary>
    public static List<FileNode> Load(string dir, IReadOnlyDictionary<string, char> status)
    {
        var nodes = new List<FileNode>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return nodes;

        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(d);
                if (Ignored.Contains(name)) continue;

                var node = new FileNode
                {
                    FullPath = d,
                    Name = name,
                    IsDirectory = true,
                    Status = DirStatus(d, status)
                };
                node.Children.Add(Placeholder());
                nodes.Add(node);
            }

            foreach (var f in Directory.GetFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var node = new FileNode
                {
                    FullPath = f,
                    Name = Path.GetFileName(f),
                    IsDirectory = false
                };
                if (status.TryGetValue(Path.GetFullPath(f), out var code)) node.Status = code;
                nodes.Add(node);
            }
        }
        catch { /* unreadable folder (permissions, race) - show what we have */ }

        return nodes;
    }

    /// <summary>Mark a folder modified ('M') if any changed path lives beneath it.</summary>
    private static char DirStatus(string dir, IReadOnlyDictionary<string, char> status)
    {
        if (status.Count == 0) return '\0';
        var prefix = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var key in status.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 'M';
        return '\0';
    }
}
