using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StayVibin.Services;

/// <summary>
/// How the agent should plan before acting. The operator sets this; the agent is
/// told about the active mode in its system prompt.
/// </summary>
public enum PlanMode
{
    /// <summary>No planning gate - the agent just does the work.</summary>
    Off,
    /// <summary>Default: the agent asks the operator whether to plan first.</summary>
    Ask,
    /// <summary>The agent decides on its own when a task is complex enough to plan first.</summary>
    Auto,
    /// <summary>Every task starts with a plan the operator must approve.</summary>
    Always
}

/// <summary>
/// Operator permission mode for potentially risky agent actions. Ask maps to the
/// OpenHands confirmation policy; AllowAll maps to NeverConfirm.
/// </summary>
public enum PermissionMode
{
    Ask,
    AllowAll
}

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
    /// <summary>
    /// Runtime context window (tokens). 0 means "auto" (the Settings box is empty):
    /// when AutoTune is on the model's native window is used, otherwise a 32k default
    /// applies. A positive value is an explicit override that always wins. This is the
    /// number shown on the context meter and passed to the agent (num_ctx /
    /// OLLAMA_CONTEXT_LENGTH).
    /// </summary>
    public int ContextLength { get; set; }   // 0 = auto

    /// <summary>Context used when "auto" can't resolve to a model window.</summary>
    public const int FallbackContextLength = 32768;

    /// <summary>
    /// Concrete context for the backend launch env var. Per-request num_ctx still
    /// overrides this when AutoTune sizes a specific model, so the 32k auto baseline
    /// here is just a floor for the server's global default.
    /// </summary>
    public int BackendContextLength => ContextLength > 0 ? ContextLength : FallbackContextLength;
    /// <summary>Maximum agent loop iterations per conversation.</summary>
    public int MaxIterations { get; set; } = 500;

    /// <summary>
    /// When true, picking/starting a model auto-applies sensible temperature,
    /// reasoning effort and context size detected from Ollama (newbie-friendly).
    /// </summary>
    public bool AutoTune { get; set; } = true;

    /// <summary>
    /// How the agent plans before acting. Stored as a string for a readable,
    /// version-stable settings.json. Defaults to Ask so first-timers are walked
    /// through "plan or just go" instead of getting surprise changes.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PlanMode PlanMode { get; set; } = PlanMode.Ask;

    /// <summary>
    /// Whether potentially risky agent actions require operator confirmation.
    /// Defaults to Ask for human-friendly safety; power users can switch to AllowAll.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Ask;

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
        // Allow 0 (auto). Any other sub-floor value is coerced to auto.
        if (ContextLength is not 0 and < 1024) ContextLength = 0;
        if (MaxIterations < 1) MaxIterations = 500;
        if (string.IsNullOrWhiteSpace(Host)) Host = "127.0.0.1";
        if (string.IsNullOrWhiteSpace(OllamaUrl)) OllamaUrl = "http://localhost:11434";
        return this;
    }
}
