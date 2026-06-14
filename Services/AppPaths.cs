using System.IO;

namespace StayVibin.Services;

/// <summary>Central place for the app's on-disk locations (settings + logs).</summary>
public static class AppPaths
{
    private const string FolderName = "StayVibin";
    private const string LegacyFolderName = "OpenHandsDesktop";

    public static string Root
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, FolderName);
            MigrateLegacy(appData, dir);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// One-time move of the old app data folder to the StayVibin folder so existing
    /// settings/logs carry over. No-op afterwards. Keep the literal legacy folder
    /// name above for backwards-compatible migration.
    /// </summary>
    private static void MigrateLegacy(string appData, string newDir)
    {
        try
        {
            var legacy = Path.Combine(appData, LegacyFolderName);
            if (Directory.Exists(legacy) && !Directory.Exists(newDir))
                Directory.Move(legacy, newDir);
        }
        catch { /* if migration fails, we just start fresh */ }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(Root, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Stable folder where the agent-server persists conversations (state + event
    /// logs). Passed to the server via OH_CONVERSATIONS_PATH so chats survive across
    /// app restarts and are not tied to the server's transient launch directory.
    /// </summary>
    public static string ConversationsDir
    {
        get
        {
            var dir = Path.Combine(Root, "conversations");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string NewServerLogPath()
        => Path.Combine(LogsDir, $"server-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static string NewEngineLogPath()
        => Path.Combine(LogsDir, $"engine-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static string CrashLogPath => Path.Combine(LogsDir, "crash.log");
}
