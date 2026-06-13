using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StayVibin.Models;

/// <summary>
/// One node in the workspace explorer tree (a file or a folder). Folders load
/// their children lazily: they start with a single placeholder child so the
/// expander arrow shows, and the real children are filled in on first expand.
/// </summary>
public sealed class FileNode : INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }

    /// <summary>True for the lazy-load stand-in child (never shown to the user).</summary>
    public bool IsPlaceholder { get; init; }

    public ObservableCollection<FileNode> Children { get; } = new();

    /// <summary>Folder/file glyph for the row.</summary>
    public string Icon => IsDirectory ? "\U0001F4C1" : "\U0001F4C4";

    private char _status;   // git porcelain code: 'M', 'A', 'D', 'R', '?', or '\0' (clean)

    /// <summary>Raw git status code for this path ('\0' when clean/unknown).</summary>
    public char Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Short letter shown after the name (U = untracked, M, A, D, R).</summary>
    public string StatusText => _status switch
    {
        'M' => "M",
        'A' => "A",
        'D' => "D",
        'R' => "R",
        '?' => "U",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
