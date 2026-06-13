namespace StayVibin.Models;

/// <summary>How an attachment is treated: generic file, viewable image, or video.</summary>
public enum AttachKind { File, Image, Video }

/// <summary>A file the user attached to the next message.</summary>
public sealed class Attachment
{
    public required string SourcePath { get; init; }
    public required string DestAbsPath { get; init; }
    public required string DestRelPath { get; init; }  // relative to working dir (forward slashes)
    public required AttachKind Kind { get; init; }

    public string FileName => System.IO.Path.GetFileName(DestAbsPath);
}
