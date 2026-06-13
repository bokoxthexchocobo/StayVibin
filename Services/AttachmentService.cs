using System.Diagnostics;
using System.Globalization;
using System.IO;
using StayVibin.Models;

namespace StayVibin.Services;

/// <summary>
/// Handles user attachments: copies them into the agent's working directory so
/// the agent can open them with its tools, turns images into data URLs for
/// vision models, and (best-effort, if ffmpeg is installed) extracts frames and
/// metadata from videos so vision models can "watch" them.
/// </summary>
public static class AttachmentService
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg" };

    public const string UploadsFolder = "uploads";

    public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExt.Contains(Path.GetExtension(path));

    public static AttachKind KindOf(string path)
        => IsImage(path) ? AttachKind.Image : IsVideo(path) ? AttachKind.Video : AttachKind.File;

    /// <summary>Copy a file into &lt;workingDir&gt;/uploads (unless it already lives in the working dir).</summary>
    public static Attachment Stage(string sourcePath, string workingDir)
    {
        var kind = KindOf(sourcePath);

        string destAbs;
        if (IsInside(sourcePath, workingDir))
        {
            destAbs = Path.GetFullPath(sourcePath);
        }
        else
        {
            var dir = Path.Combine(workingDir, UploadsFolder);
            Directory.CreateDirectory(dir);
            destAbs = UniquePath(Path.Combine(dir, Path.GetFileName(sourcePath)));
            File.Copy(sourcePath, destAbs, overwrite: false);
        }

        var rel = Path.GetRelativePath(workingDir, destAbs).Replace('\\', '/');
        return new Attachment
        {
            SourcePath = sourcePath,
            DestAbsPath = destAbs,
            DestRelPath = rel,
            Kind = kind
        };
    }

    public static string ToDataUrl(string imagePath)
    {
        var mime = Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };
        var b64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
        return $"data:{mime};base64,{b64}";
    }

    // ---- video (best-effort, needs ffmpeg on PATH) --------------------------

    private static bool? _ffmpeg;
    public static bool FfmpegAvailable => _ffmpeg ??= ProbeTool("ffmpeg");

    private static bool ProbeTool(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            // Drain the pipes so the child never blocks writing to a full buffer.
            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Human-readable ffprobe metadata for a video, or null.</summary>
    public static async Task<string?> ProbeVideoAsync(string videoPath)
    {
        if (!FfmpegAvailable) return null;
        var args = "-v error -show_entries format=duration,size:stream=codec_type,codec_name,width,height,avg_frame_rate "
                   + $"-of default=noprint_wrappers=1 \"{videoPath}\"";
        var (ok, output) = await RunAsync("ffprobe", args);
        return ok && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    /// <summary>
    /// Extract up to <paramref name="count"/> evenly spaced frames as image data
    /// URLs so a vision model can see the video. Empty if ffmpeg is unavailable.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExtractFramesAsync(string videoPath, int count = 6)
    {
        if (!FfmpegAvailable) return Array.Empty<string>();

        var duration = await GetDurationAsync(videoPath);
        var temp = Path.Combine(Path.GetTempPath(), "ohd-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var urls = new List<string>();

        try
        {
            if (duration > 1)
            {
                for (int i = 0; i < count; i++)
                {
                    var t = duration * (i + 1) / (count + 1);
                    var outPath = Path.Combine(temp, $"f{i:00}.jpg");
                    var args = $"-ss {t.ToString("0.000", CultureInfo.InvariantCulture)} -i \"{videoPath}\" "
                               + $"-frames:v 1 -q:v 3 -vf scale=768:-2 -y \"{outPath}\"";
                    var (ok, _) = await RunAsync("ffmpeg", args);
                    if (ok && File.Exists(outPath)) urls.Add(ToDataUrl(outPath));
                }
            }
            else
            {
                // Unknown/short duration: grab a handful by frame index.
                var pattern = Path.Combine(temp, "f%02d.jpg");
                var args = $"-i \"{videoPath}\" -vf \"thumbnail,scale=768:-2\" -frames:v {count} -y \"{pattern}\"";
                await RunAsync("ffmpeg", args);
                foreach (var f in Directory.GetFiles(temp, "*.jpg").OrderBy(x => x))
                    urls.Add(ToDataUrl(f));
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
        return urls;
    }

    private static async Task<double> GetDurationAsync(string videoPath)
    {
        var (ok, output) = await RunAsync("ffprobe",
            $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{videoPath}\"");
        return ok && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0;
    }

    private static async Task<(bool ok, string output)> RunAsync(string tool, string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
            // Read both pipes concurrently to avoid a pipe-buffer deadlock - ffmpeg
            // in particular is chatty on stderr while we read stdout.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } return (false, ""); }
            return (p.ExitCode == 0, stdoutTask.Result);
        }
        catch
        {
            return (false, "");
        }
    }

    private static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var baseDir = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
