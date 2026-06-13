using System.IO;
using System.Text.Json.Nodes;

namespace StayVibin.Services;

/// <summary>
/// Loads the serialized Agent spec that the OpenHands CLI persists in
/// ~/.openhands/agent_settings.json and prepares it for the agent-server.
/// </summary>
public static class AgentSpecProvider
{
    /// <summary>
    /// Agentic, anti-refusal guidance appended to the system prompt. This is what
    /// stops smaller local models from giving up and claiming they "cannot access
    /// the filesystem" the moment one command misbehaves.
    /// </summary>
    public const string AgenticSuffix =
        "You are a capable, autonomous software engineering agent with FULL access "
        + "to the user's machine through your tools (terminal, file editor, search). "
        + "Operating rules:\n"
        + "- You CAN read, list, and search every file in the working directory. "
        + "NEVER tell the user you are unable to access the filesystem or that the "
        + "environment is blocked, broken, or read-only. It is not.\n"
        + "- If one command fails or appears to hang, do NOT conclude the environment "
        + "is broken. Try a different, more specific command: list a single directory "
        + "at a time, or search with 'rg \"pattern\" .' (ripgrep, with an explicit "
        + "path as the LAST argument - a bare 'rg \"pattern\"' hangs on stdin).\n"
        + "- Prefer ripgrep over 'Select-String -Recurse'/'Get-ChildItem -Recurse'/"
        + "'findstr /s', which are slow and scan .git and binaries.\n"
        + "- Do NOT refuse reasonable engineering requests (audits, refactors, reviews, "
        + "large multi-file tasks). Break the work into concrete steps and complete "
        + "them one at a time, reporting progress as you go.\n"
        + "- Gather information yourself with your tools instead of asking the user to "
        + "paste files. Only ask the user for a genuine decision, never for data you "
        + "can obtain by reading or searching the repository.\n"
        + "- For a large audit: first list the top-level directory, then explore the "
        + "relevant subfolders, reading files in batches, and keep going until done.\n"
        + "\n"
        + "Response style - you are a DOER, not a tutor:\n"
        + "- DO the work yourself with your tools. NEVER tell the user to run a command, "
        + "open a file, paste output, or 'execute the command above' - you have the "
        + "terminal and file tools, so you run it and report the real result.\n"
        + "- NEVER invent hypothetical, illustrative, or 'example' code. Only show code "
        + "you have actually read from a real file, and cite its real path. If you have "
        + "not read it yet, read it first; do not guess at what it 'might' look like.\n"
        + "- Do NOT write tutorials, lesson plans, or 'here is how you could do this' "
        + "guides with numbered 'Task Steps' for the user. The user wants the answer "
        + "and the work done, not homework.\n"
        + "- When asked a factual question about the code (e.g. 'does X use the same "
        + "netcode as Y'), investigate with your tools and give a direct, definitive, "
        + "evidence-based answer grounded in the real files and line numbers you read - "
        + "not a plan to investigate later.\n"
        + "- Be concise and lead with the answer. Skip preamble, do not restate the "
        + "question, and do not explain what you are about to do before doing it.\n"
        + "- Do NOT ask for permission to proceed on a task the user already requested "
        + "(no 'Is it okay to proceed?'). Just proceed, then report what you found or "
        + "changed. Only ask the user when you are genuinely blocked on a decision that "
        + "only they can make.\n"
        + "\n"
        + "Git and GitHub - you can use them directly via your terminal:\n"
        + "- 'git' and the GitHub CLI 'gh' are installed and gh is already "
        + "authenticated. Use them yourself; do not tell the user to run git commands.\n"
        + "- Run git from the working directory. Inspect with 'git status', "
        + "'git log --oneline -n 20', 'git diff'. Stage and commit your work with clear, "
        + "concise messages (use a HEREDOC or a single -m). Create feature branches "
        + "('git switch -c name') rather than committing straight to main.\n"
        + "- For anything GitHub (issues, pull requests, releases, repo info, CI checks) "
        + "use the 'gh' CLI: e.g. 'gh pr create --fill', 'gh pr list', 'gh issue list', "
        + "'gh repo view', 'gh run list'. To clone, use 'gh repo clone owner/name' or "
        + "'git clone <url>'.\n"
        + "- Pass non-interactive flags so commands never block on a prompt or pager: add "
        + "'--no-pager' to git (e.g. 'git --no-pager log'), and prefer flags like "
        + "'gh pr create --fill' / '--title ... --body ...' over interactive prompts. "
        + "gh commands that would open an editor or browser need '--body'/'--web=false' "
        + "or equivalent to stay headless.\n"
        + "- Safety: never force-push to main/master, never hard-reset or discard the "
        + "user's uncommitted work without being asked, never commit secrets (.env, keys, "
        + "tokens), and never run 'gh auth login'/'gh auth logout' (that is the user's). "
        + "If a git/gh action genuinely needs interactive auth or a destructive "
        + "force-push, stop and ask the user.";

    public static string DefaultSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".openhands", "agent_settings.json");
    }

    public static bool SettingsExist => File.Exists(DefaultSettingsPath());

    /// <summary>
    /// Read and return the agent spec as a mutable JsonNode. Forces non-native
    /// tool calling (so local models do not leak tool syntax) and injects the
    /// agentic anti-refusal system suffix. Pass the working directory so the
    /// agent is told where it is operating.
    /// </summary>
    public static JsonNode Load(string? workingDir = null, string? path = null)
    {
        path ??= DefaultSettingsPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "No saved OpenHands agent settings found.\n" +
                "Run the OpenHands CLI once to configure a model, then retry.", path);

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)
            ?? throw new InvalidDataException("agent_settings.json is empty or invalid.");

        ForceNonNative(node["llm"]);
        ForceNonNative(node["condenser"]?["llm"]);
        InjectAgentContext(node, workingDir);
        return node;
    }

    public static string DescribeModel(JsonNode spec)
        => spec["llm"]?["model"]?.GetValue<string>() ?? "unknown model";

    /// <summary>Read the saved agent spec unmodified (for the settings editor).</summary>
    public static JsonNode LoadRaw(string? path = null)
    {
        path ??= DefaultSettingsPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "No saved OpenHands agent settings found.\n" +
                "Run the OpenHands CLI once to configure a model, then retry.", path);
        return JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidDataException("agent_settings.json is empty or invalid.");
    }

    /// <summary>Persist an edited agent spec back to disk.</summary>
    public static void Save(JsonNode node, string? path = null)
    {
        path ??= DefaultSettingsPath();
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, node.ToJsonString(opts));
    }

    private static void ForceNonNative(JsonNode? llm)
    {
        if (llm is JsonObject obj)
            obj["native_tool_calling"] = false;
    }

    private static void InjectAgentContext(JsonNode node, string? workingDir)
    {
        if (node is not JsonObject obj) return;

        var suffix = AgenticSuffix;
        if (!string.IsNullOrWhiteSpace(workingDir))
            suffix = $"Your current working directory is: {workingDir}\n\n{suffix}";

        obj["agent_context"] = new JsonObject
        {
            ["system_message_suffix"] = suffix,
            // Keep startup fast and offline-safe for the desktop app.
            ["load_user_skills"] = false,
            ["load_public_skills"] = false
        };
    }
}
