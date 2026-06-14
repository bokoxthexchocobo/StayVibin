using System;
using System.IO;

namespace StayVibin.Models;

/// <summary>
/// One row in the conversation-history sidebar. A lightweight display wrapper over
/// a persisted server conversation (see <see cref="Services.ConversationSummary"/>).
/// </summary>
public sealed class ConversationRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string WorkingDir { get; init; } = "";
    public DateTime UpdatedAt { get; init; }

    /// <summary>Server-generated/user title when present, else a friendly fallback.</summary>
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? "New chat" : Title.Trim();

    /// <summary>Folder name plus a relative time, e.g. "MyRepo - 2h ago".</summary>
    public string Subtitle
    {
        get
        {
            var folder = string.IsNullOrWhiteSpace(WorkingDir)
                ? ""
                : Path.GetFileName(WorkingDir.TrimEnd('\\', '/'));
            var when = RelativeTime(UpdatedAt);
            if (string.IsNullOrEmpty(folder)) return when;
            return string.IsNullOrEmpty(when) ? folder : $"{folder} - {when}";
        }
    }

    private static string RelativeTime(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "";
        var local = utc.ToLocalTime();
        var span = DateTime.Now - local;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return local.ToString("MMM d");
    }
}
