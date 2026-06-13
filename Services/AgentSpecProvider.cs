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
        + "\n"
        + "Windows PowerShell terminal - follow these exactly to avoid jamming the shell:\n"
        + "- This machine runs Windows PowerShell. Chain commands with ';' - NEVER use "
        + "'&&' or '||'. They are parse errors in PowerShell ('the token && is not a "
        + "valid statement separator') and they break the command wrapper, which jams "
        + "the session so that EVERY later command is rejected with 'the previous "
        + "command is still running'.\n"
        + "- Run ONE command per action. Use native cmdlets / aliases: 'Get-ChildItem' "
        + "(ls/dir), 'Get-Content' (cat), 'Set-Location' (cd), 'Get-Location' (pwd).\n"
        + "- Recovering a stuck terminal: if a terminal result has exit code -1, or says "
        + "'no new output after 60 seconds', or 'the previous command is still running "
        + "/ is NOT executed', the shell is busy waiting on the PREVIOUS command. Do NOT "
        + "keep sending new commands - they will all be rejected and you will loop. "
        + "Instead send exactly ONE terminal action with is_input=true and command "
        + "'C-c' to interrupt it, then continue with your next real command.\n"
        + "- NEVER set reset=true together with is_input=true - that combination errors "
        + "out ('Cannot use reset=True with is_input=True'). To get a fresh shell, send "
        + "reset=true with a normal command and is_input=false.\n"
        + "- Never send the same command again after it fails. If an approach fails twice, "
        + "STOP and change the approach - do not repeat yourself in a loop.\n"
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
        + "- 'git' and the GitHub CLI 'gh' may be on PATH. Run 'git status' and "
        + "'gh auth status' yourself to see what is available; do not assume GitHub "
        + "access until gh reports a signed-in account.\n"
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
        + "force-push, stop and ask the user."
        + "\n\n"
        + "Accuracy - the codebase is ground truth (Cursor/IDE rules):\n"
        + "- NEVER describe, quote, compare, or edit code you have not read with your "
        + "file tools in this session. Search or open the file first.\n"
        + "- Before answering a factual question about the code, locate the relevant "
        + "symbols with 'rg \"pattern\" .' or by reading the named file, then answer "
        + "from what you actually read.\n"
        + "- When you state a fact about the code, cite evidence: path:line or a short "
        + "quote copied from the file you read. No hypothetical snippets.\n"
        + "- If you searched and did not find something, say exactly what you searched "
        + "and where - do not guess that it 'might' exist elsewhere.\n"
        + "- Before editing, read the target file (or the relevant section). Prefer "
        + "small, precise edits over rewriting whole files.\n"
        + "- Do not infer behavior from filenames, folder names, or naming similarity "
        + "alone - read the implementation.\n"
        + "- After non-trivial edits, verify with 'git diff' or re-read the changed "
        + "lines when feasible.\n"
        + "- StayVibin may tell you which file the user has open in the editor; treat "
        + "that as a hint for what they care about, but still read the file before "
        + "claiming what is in it.";

    /// <summary>
    /// Sentinel the agent prints on its own line when a plan is ready for the
    /// operator to approve. StayVibin detects this to show the Approve/Request-
    /// changes bar and strips it from the visible message.
    /// </summary>
    public const string PlanReadyMarker = "<<PLAN_READY>>";

    /// <summary>
    /// Build the plan-mode instructions appended to the agent's system prompt for
    /// the operator's chosen <see cref="PlanMode"/>. Returns "" for Off. All gated
    /// modes tell the agent to investigate read-only, present a numbered plan that
    /// ends with <see cref="PlanReadyMarker"/>, then stop until the operator
    /// approves before making any changes.
    /// </summary>
    public static string PlanModeSuffix(PlanMode mode)
    {
        if (mode == PlanMode.Off) return "";

        // Shared rules for every gated mode: what "a plan" means and how to hand it
        // back for approval. The marker MUST be exact so the UI can detect it.
        const string common =
            "When you plan, first investigate read-only (read and search files - do NOT "
            + "edit anything yet), then present a concise, numbered PLAN of exactly what "
            + "you intend to change and why. Also record it with the task_tracker 'plan' "
            + "command. End the plan message with the exact line " + PlanReadyMarker + " on "
            + "its own line, then STOP and wait. Do NOT edit files, run state-changing "
            + "commands, or perform git writes until the operator approves. The operator "
            + "approves by clicking Approve or replying with something like 'approve', "
            + "'go ahead', or 'yes' - treat any such reply as approval and carry out the "
            + "plan, making the changes. If they ask for changes instead, revise the plan "
            + "and present it again ending with " + PlanReadyMarker + ".";

        return mode switch
        {
            PlanMode.Always =>
                "PLAN MODE (Always on): Every task must start with a plan the operator "
                + "approves before you make any changes. " + common,
            PlanMode.Auto =>
                "PLAN MODE (Auto): For any non-trivial task (multiple steps or files, "
                + "refactors, deletions, or anything risky), plan first. For a tiny, "
                + "obviously-safe task or a plain question, you may proceed without a "
                + "plan. " + common,
            PlanMode.Ask =>
                "PLAN MODE (Ask): At the START of a new task, briefly ask the operator "
                + "whether you should plan first or just proceed, and wait for their "
                + "answer. If they want a plan (or the task is risky), plan first. If "
                + "they say go ahead, proceed directly. " + common,
            _ => ""
        };
    }

    public static string DefaultSettingsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".openhands", "agent_settings.json");
    }

    public static bool SettingsExist => File.Exists(DefaultSettingsPath());

    /// <summary>
    /// Write a fresh agent_settings.json for a local OpenAI-compatible provider
    /// (currently Ollama) so first-run users never have to run the OpenHands CLI.
    /// The schema mirrors what the CLI persists; only the model and base URL vary.
    /// </summary>
    public static void CreateDefault(string model, string providerBaseUrl, string? path = null)
    {
        path ??= DefaultSettingsPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var baseUrl = NormalizeOpenAiBaseUrl(providerBaseUrl);
        var modelField = model.Contains('/') ? model : "openai/" + model;

        var spec = new JsonObject
        {
            ["llm"] = BuildLlm(modelField, baseUrl, "agent"),
            ["tools"] = new JsonArray(
                ToolNode("terminal"), ToolNode("file_editor"),
                ToolNode("task_tracker"), ToolNode("task_tool_set")),
            ["filter_tools_regex"] = null,
            ["include_default_tools"] = new JsonArray("FinishTool", "ThinkTool"),
            ["agent_context"] = null,
            ["system_prompt"] = null,
            ["system_prompt_filename"] = "system_prompt.j2",
            ["security_policy_filename"] = "security_policy.j2",
            ["system_prompt_kwargs"] = new JsonObject { ["llm_security_analyzer"] = true },
            ["condenser"] = new JsonObject
            {
                ["llm"] = BuildLlm(modelField, baseUrl, "condenser"),
                ["max_size"] = 240,
                ["max_tokens"] = null,
                ["keep_first"] = 2,
                ["minimum_progress"] = 0.1,
                ["hard_context_reset_max_retries"] = 5,
                ["hard_context_reset_context_scaling"] = 0.8,
                ["kind"] = "LLMSummarizingCondenser"
            },
            ["critic"] = null,
            ["tool_concurrency_limit"] = 1,
            ["kind"] = "Agent"
        };

        Save(spec, path);
    }

    private static JsonObject ToolNode(string name)
        => new() { ["name"] = name, ["params"] = new JsonObject() };

    /// <summary>Build one LLM block matching the CLI's agent_settings.json schema.</summary>
    private static JsonObject BuildLlm(string modelField, string baseUrl, string usageId) => new()
    {
        ["model"] = modelField,
        ["api_key"] = "local-llm",
        ["base_url"] = baseUrl,
        ["api_version"] = null,
        ["openrouter_site_url"] = "https://docs.all-hands.dev/",
        ["openrouter_app_name"] = "OpenHands",
        ["num_retries"] = 5,
        ["retry_multiplier"] = 8.0,
        ["retry_min_wait"] = 8,
        ["retry_max_wait"] = 64,
        ["timeout"] = 300,
        ["max_message_chars"] = 30000,
        ["temperature"] = null,
        ["top_p"] = null,
        ["top_k"] = null,
        ["max_input_tokens"] = null,
        ["max_output_tokens"] = null,
        ["stream"] = false,
        ["drop_params"] = true,
        ["modify_params"] = true,
        ["disable_stop_word"] = false,
        ["caching_prompt"] = true,
        ["log_completions"] = false,
        ["log_completions_folder"] = "logs\\completions",
        ["native_tool_calling"] = true,
        ["reasoning_effort"] = "high",
        ["enable_encrypted_reasoning"] = true,
        ["prompt_cache_retention"] = "24h",
        ["extended_thinking_budget"] = 200000,
        ["seed"] = null,
        ["usage_id"] = usageId,
        ["litellm_extra_body"] = new JsonObject()
    };

    /// <summary>Coerce a provider URL into the OpenAI-compatible '.../v1' form.</summary>
    private static string NormalizeOpenAiBaseUrl(string url)
    {
        var u = (url ?? "").Trim().TrimEnd('/');
        if (u.Length == 0) u = "http://localhost:11434";
        if (!u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u += "/v1";
        return u;
    }

    /// <summary>
    /// Read and return the agent spec as a mutable JsonNode and inject the agentic
    /// anti-refusal system suffix. Pass the working directory so the agent is told
    /// where it is operating. Tool-calling mode is left to the saved spec / AutoTune
    /// (which enables native calling for models that support it); we no longer force
    /// the prompt-text fallback, which made capable models leak markup and loop.
    /// </summary>
    public static JsonNode Load(string? workingDir = null, string? workspaceSnapshot = null,
        string? path = null, PlanMode planMode = PlanMode.Off)
    {
        path ??= DefaultSettingsPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "No saved OpenHands agent settings found.\n" +
                "Run the OpenHands CLI once to configure a model, then retry.", path);

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)
            ?? throw new InvalidDataException("agent_settings.json is empty or invalid.");

        ApplyAccuracySettings(node);
        InjectAgentContext(node, workingDir, workspaceSnapshot, planMode);
        return node;
    }

    /// <summary>Same as <see cref="Load"/> but builds the workspace snapshot first.</summary>
    public static async Task<JsonNode> LoadAsync(
        string? workingDir = null, string? editorPath = null, string? path = null,
        PlanMode planMode = PlanMode.Off)
    {
        var snapshot = workingDir is null
            ? ""
            : await WorkspaceContextService.BuildAsync(workingDir, editorPath);
        return Load(workingDir, string.IsNullOrWhiteSpace(snapshot) ? null : snapshot, path, planMode);
    }

    /// <summary>
    /// Loosen condensation slightly so long investigations retain more early context
    /// (helps local models stay accurate on multi-step tasks).
    /// </summary>
    public static void ApplyAccuracySettings(JsonNode node)
    {
        if (node["condenser"] is not JsonObject cond) return;

        var keep = cond["keep_first"]?.GetValue<int>() ?? 2;
        if (keep < 4) cond["keep_first"] = 4;

        var max = cond["max_size"]?.GetValue<int>() ?? 240;
        if (max < 280) cond["max_size"] = 280;
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

    private static void InjectAgentContext(JsonNode node, string? workingDir,
        string? workspaceSnapshot, PlanMode planMode = PlanMode.Off)
    {
        if (node is not JsonObject obj) return;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceSnapshot))
            parts.Add(workspaceSnapshot.Trim());
        if (!string.IsNullOrWhiteSpace(workingDir))
            parts.Add($"Your current working directory is: {workingDir}");
        parts.Add(AgenticSuffix);

        var planSuffix = PlanModeSuffix(planMode);
        if (!string.IsNullOrEmpty(planSuffix))
            parts.Add(planSuffix);

        obj["agent_context"] = new JsonObject
        {
            ["system_message_suffix"] = string.Join("\n\n", parts),
            // Keep startup fast and offline-safe for the desktop app.
            ["load_user_skills"] = false,
            ["load_public_skills"] = false
        };
    }
}
