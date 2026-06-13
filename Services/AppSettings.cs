using System.IO;
using System.Text.Json;

namespace StayVibin.Services;

/// <summary>
/// App-level preferences (connection + behavior) persisted to
/// %APPDATA%\StayVibin\settings.json. Model/LLM details live in
/// ~/.openhands/agent_settings.json and are edited separately.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Interface the agent-server binds to (loopback by default).</summary>
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>TCP port for the agent-server.</summary>
    public int Port { get; set; } = 8000;
    /// <summary>Base URL of the local Ollama instance.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    /// <summary>Explicit agent-server.exe path; empty = auto-detect the uv install.</summary>
    public string AgentServerPath { get; set; } = "";
    /// <summary>Default working folder for new sessions; empty = user profile.</summary>
    public string DefaultWorkingDir { get; set; } = "";
    /// <summary>Token context window passed to the server (OLLAMA_CONTEXT_LENGTH).</summary>
    public int ContextLength { get; set; } = 32768;
    /// <summary>Maximum agent loop iterations per conversation.</summary>
    public int MaxIterations { get; set; } = 500;

    /// <summary>
    /// When true, picking/starting a model auto-applies sensible temperature,
    /// reasoning effort and context size detected from Ollama (newbie-friendly).
    /// </summary>
    public bool AutoTune { get; set; } = true;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            var path = AppPaths.SettingsFile;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s is not null) return s.Normalized();
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings().Normalized();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* non-fatal */ }
    }

    public string EffectiveWorkingDir =>
        string.IsNullOrWhiteSpace(DefaultWorkingDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : DefaultWorkingDir;

    public string EffectiveAgentServerPath =>
        string.IsNullOrWhiteSpace(AgentServerPath)
            ? BackendManager.DefaultExecutablePath()
            : AgentServerPath;

    private AppSettings Normalized()
    {
        if (Port is <= 0 or > 65535) Port = 8000;
        if (ContextLength < 1024) ContextLength = 32768;
        if (MaxIterations < 1) MaxIterations = 500;
        if (string.IsNullOrWhiteSpace(Host)) Host = "127.0.0.1";
        if (string.IsNullOrWhiteSpace(OllamaUrl)) OllamaUrl = "http://localhost:11434";
        return this;
    }
}
