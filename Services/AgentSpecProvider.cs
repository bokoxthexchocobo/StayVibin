using System.IO;
using System.Text.Json.Nodes;

namespace StayVibin.Services;

/// <summary>
/// Loads the serialized Agent spec that StayVibin's engine persists in
/// ~/.openhands/agent_settings.json and prepares it for the agent-server.
/// </summary>
public static class AgentSpecProvider
{
    /// <summary>
    /// Short, accuracy-first prompt for smaller local models. These models lose
    /// reliability when the system prompt is huge, so keep the rules compact and
    /// concrete: use tools, read before claiming, cite paths, and avoid essays.
    /// </summary>
    public const string CompactAgenticSuffix =
        "You are a capable local coding agent with full tool access.\n"
        + "- Use tools, do not only describe what you would do.\n"
        + "- For code questions, search/read the repository first, then answer from what you read.\n"
        + "- For broad requests, map the repo first: list top folders, use glob/grep broadly, then read likely files.\n"
        + "- The file viewer can show large chunks; use view_range [start, -1] to continue reading instead of claiming a tiny line cap.\n"
        + "- Never invent code you have not opened.\n"
        + "- Cite real file paths when stating facts.\n"
        + "- Prefer these tools first: grep for content, glob for file names, file_editor to read files, terminal to run commands.\n"
        + "- If a search returns nothing, widen it instead of repeating the same call.\n"
        + "- Keep answers short, concrete, and grounded in repository evidence.\n"
        + "- If you have not read enough to answer accurately, keep investigating with tools instead of guessing.";

    /// <summary>
    /// Agentic, anti-refusal guidance appended to the system prompt. This is what
    /// stops smaller local models from giving up and claiming they "cannot access
    /// the filesystem" the moment one command misbehaves.
    /// </summary>
    public const string AgenticSuffix =
        "You are a capable, autonomous software engineering agent with FULL access "
        + "to the user's machine through your tools (terminal, file editor, search). "
        + "Operating rules:\n"
        + "- Your primary tools are: 'terminal' (run a Windows PowerShell command), "
        + "'file_editor' (view/create/edit files), 'grep' (search file CONTENTS by "
        + "regex), 'glob' (find files by name/path pattern), 'task_tracker' (your "
        + "to-do list), 'think', and 'finish'. Common synonyms are accepted and "
        + "routed automatically, so you will NOT get a 'tool not found' error for "
        + "them: 'search' -> grep, 'find' -> glob, and "
        + "'bash'/'shell'/'cmd'/'powershell'/'execute_bash'/'execute_powershell'/"
        + "'run_command' -> terminal. There is still NO separate 'read_file', "
        + "'write_file', 'edit_file', 'str_replace', 'codebase_search', or 'python' "
        + "tool - use 'file_editor' to read or change files and 'grep' to search "
        + "code. To search file CONTENTS use 'grep'; to find files by NAME use "
        + "'glob'; to read or change a file use 'file_editor'; to run a shell command "
        + "use 'terminal'. StayVibin raises the default tool output ceilings: file_editor "
        + "views can return large chunks (about 64k characters), and grep/glob can return "
        + "up to 500 matching files. Do NOT say you can only read 100 lines or only inspect "
        + "a few files. If a result is clipped, continue with file_editor view_range "
        + "[next_line, -1], narrower grep searches, or targeted glob patterns.\n"
        + "- grep/glob 'path' rules (IMPORTANT - getting this wrong is the #1 reason a "
        + "search 'finds nothing'): the search is rooted at your working directory. To "
        + "search the WHOLE project, OMIT the 'path' argument entirely - that is the "
        + "most reliable option. To limit the search, pass a folder under the working "
        + "directory, e.g. path='src' or path='src/playsim', or an absolute path. Do "
        + "NOT pass paths that climb out of the project. If a search returns nothing, "
        + "do NOT immediately repeat the exact same search - widen it: drop the 'path' "
        + "to scan the whole project, simplify the pattern, or try a related term. "
        + "Never run the identical grep/glob call twice in a row.\n"
        + "- Web access: to clone or pull a GitHub repo, use the 'terminal' tool with "
        + "'git clone <url>' or 'gh repo clone owner/name'. To read a web page, if "
        + "'browser_navigate'/'browser_get_content' tools are available use them "
        + "(navigate to the URL, then get the page content); otherwise fetch with "
        + "'terminal' via 'Invoke-WebRequest <url>'. Only browse when the task needs "
        + "live web data - do not browse for things you can answer from the repo.\n"
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
        + "- To run a shell command, prefer the tool named exactly 'terminal'. The "
        + "synonyms 'execute_powershell', 'powershell', 'bash', 'shell', 'cmd', "
        + "'run_command', and 'execute_bash' are also accepted and routed to the same "
        + "terminal, so they will not error - but 'terminal' is the canonical name. "
        + "The terminal already runs commands in Windows PowerShell, so put your "
        + "PowerShell command in its 'command' argument.\n"
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
        + "- For broad codebase questions, do NOT ask the user to name specific files. "
        + "Build the general drift yourself: inspect the workspace snapshot, list the "
        + "top-level directory, glob for major project files, grep for key symbols, read "
        + "representative implementations, then narrow down. For a large audit: first "
        + "list the top-level directory, then explore the relevant subfolders, reading "
        + "files in batches, and keep going until done.\n"
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
    /// Return a prompt profile appropriate for the selected model. Small local models
    /// need a short, high-signal instruction set; larger models benefit from the full
    /// operating guide. We infer model scale from the tag and use a conservative
    /// cutoff so 7B/8B-class models stay on the compact profile.
    /// </summary>
    public static string PromptProfileForModel(string? modelField)
    {
        var model = StripProviderPrefix(modelField);
        return ModelTuning.UseCompactPromptProfile(model) ? CompactAgenticSuffix : AgenticSuffix;
    }

    private static string StripProviderPrefix(string? modelField)
    {
        var s = modelField ?? "";
        var slash = s.IndexOf('/');
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

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
    /// (currently Ollama) so first-run users never have to configure the engine by hand.
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
            // No task_tool_set: that tool lets the agent spawn SUBAGENTS, and local
            // models drive them poorly - the subagent returns empty responses and the
            // stuck-detector kills the whole turn (the "planned then did nothing"
            // symptom). The main agent does the work directly instead.
            // grep (content search) and glob (filename search) are the search tools
            // models reach for most; they ship with the engine but are not registered
            // at server startup, so we also send their module qualnames on conversation
            // creation (see PrepareConversationTools) to register them.
            ["tools"] = new JsonArray(
                ToolNode("terminal"), ToolNode("file_editor"),
                ToolNode("task_tracker"), ToolNode("grep"), ToolNode("glob")),
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
                ["max_size"] = 320,
                ["max_tokens"] = null,
                ["keep_first"] = 6,
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

    /// <summary>
    /// Built-in engine tools that StayVibin enables but the agent-server does NOT
    /// auto-register at startup (it only imports terminal/file_editor/task_tracker/
    /// browser). To use one we must (a) list it in the agent spec's tools and (b)
    /// tell the server which Python module to import so it registers - sent via the
    /// conversation request's tool_module_qualnames map. Maps tool name -> module.
    /// </summary>
    private static readonly Dictionary<string, string> EngineSearchTools =
        new(StringComparer.Ordinal)
        {
            ["grep"] = "openhands.tools.grep.definition",
            ["glob"] = "openhands.tools.glob.definition",
        };

    /// <summary>
    /// Full optional/alias map: the engine search tools above PLUS the synonym
    /// aliases registered by our <see cref="EngineExtensions"/> module (e.g.
    /// "search" -> grep, "execute_powershell" -> terminal). Used to (a) add the
    /// alias tools the backend confirmed it registered, (b) strip any optional/alias
    /// tool the backend does NOT have from the conversation spec, and (c) tell the
    /// server which Python module to import to register each tool.
    /// </summary>
    private static readonly Dictionary<string, string> OptionalToolModules =
        BuildOptionalToolModules();

    private static Dictionary<string, string> BuildOptionalToolModules()
    {
        var map = new Dictionary<string, string>(EngineSearchTools, StringComparer.Ordinal);
        foreach (var alias in EngineExtensions.AliasTools)
            map[alias] = EngineExtensions.ModuleName;
        // Web browsing. The browser toolset ships with the engine but is not
        // auto-registered and is only USABLE when a Chromium/Chrome binary exists;
        // the startup probe (list_usable_tools) gates it, so it is added to the spec
        // only when it will actually work.
        map["browser_tool_set"] = "openhands.tools.browser_use.definition";
        return map;
    }

    // The tool names the running backend actually has registered, published by
    // BackendManager after probing the engine at startup. Null until probed. We only
    // enable optional/alias tools the server confirms, because an unregistered tool
    // in the spec makes conversation creation fail. Volatile: written on a background
    // probe thread, read when a conversation is created.
    private static volatile HashSet<string>? _registeredTools;

    /// <summary>Publish the set of tool names the backend has registered (from the probe).</summary>
    public static void SetAvailableOptionalTools(IEnumerable<string> registeredToolNames)
        => _registeredTools = new HashSet<string>(registeredToolNames, StringComparer.Ordinal);

    /// <summary>
    /// Clear the probed optional/alias tool set. Used before a backend restart or a
    /// failed probe so stale tool availability from a previous server instance never
    /// leaks into a fresh conversation.
    /// </summary>
    public static void ClearAvailableOptionalTools() => _registeredTools = null;

    /// <summary>
    /// Ensure the spec lists the engine search tools (grep + glob). These are the
    /// search primitives local models reach for most; adding them here means existing
    /// saved configs (written before search tools existed) get them at load time too.
    /// Synonym aliases are NOT written here - they are injected per-conversation by
    /// <see cref="PrepareConversationTools"/> only when the backend confirms them.
    /// </summary>
    private static void AddSearchTools(JsonNode node)
    {
        if (node["tools"] is not JsonArray tools) return;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
        {
            var n = t?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(n)) present.Add(n!);
        }

        foreach (var name in EngineSearchTools.Keys)
            if (!present.Contains(name))
                tools.Add(ToolNode(name));
    }

    /// <summary>
    /// Finalize the spec's tool list for a new conversation and return the
    /// module-import map for the server. Adds the optional/alias tools the backend
    /// actually registered (probed at startup) and strips any optional/alias tool it
    /// does NOT have, so the server never fails conversation creation on an
    /// unregistered tool. The returned map tells the server which Python module to
    /// import to register each optional/alias tool that remains in the spec.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PrepareConversationTools(JsonNode spec)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (spec["tools"] is not JsonArray tools) return map;

        if (UseCompactToolset(spec))
        {
            for (int i = tools.Count - 1; i >= 0; i--)
            {
                var n = tools[i]?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(n) && !CompactToolNames.Contains(n!))
                    tools.RemoveAt(i);
            }
        }

        var allowed = AllowedOptionalTools();

        // Drop optional/alias tools the backend cannot provide this session.
        for (int i = tools.Count - 1; i >= 0; i--)
        {
            var n = tools[i]?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(n)
                && OptionalToolModules.ContainsKey(n!)
                && !allowed.Contains(n!))
                tools.RemoveAt(i);
        }

        // Add allowed optional/alias tools that are not already listed.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
        {
            var n = t?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(n)) present.Add(n!);
        }
        foreach (var name in allowed)
            if (present.Add(name))
                tools.Add(ToolNode(name));

        // Build the import map for every optional/alias tool now in the spec.
        foreach (var t in tools)
        {
            var n = t?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(n) && OptionalToolModules.TryGetValue(n!, out var mod))
                map[n!] = mod;
        }
        return map;
    }

    /// <summary>
    /// Which optional/alias tools may be enabled this session. If the engine has been
    /// probed, allow exactly the ones it registered; otherwise fall back to the
    /// engine search tools (grep/glob) only - never the aliases - so we never enable
    /// something the server might not have.
    /// </summary>
    private static HashSet<string> AllowedOptionalTools()
    {
        var probed = _registeredTools;
        if (probed is null)
            return new HashSet<string>(EngineSearchTools.Keys, StringComparer.Ordinal);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in OptionalToolModules.Keys)
            if (probed.Contains(name))
                set.Add(name);
        return set;
    }

    private static bool UseCompactToolset(JsonNode spec)
    {
        var modelField = spec["llm"]?["model"]?.GetValue<string>();
        var model = StripProviderPrefix(modelField);
        return ModelTuning.UseCompactToolset(model);
    }

    private static readonly HashSet<string> CompactToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "terminal", "file_editor", "grep", "glob", "task_tracker",
            "search", "find", "read_file", "open_file", "view_file",
            "list_dir", "list_files", "codebase_search",
            "FinishTool", "ThinkTool",
        };

    /// <summary>Build one LLM block matching the CLI's agent_settings.json schema.</summary>
    private static JsonObject BuildLlm(string modelField, string baseUrl, string usageId) => new()
    {
        ["model"] = modelField,
        ["api_key"] = "local-llm",
        ["base_url"] = baseUrl,
        ["api_version"] = null,
        ["openrouter_site_url"] = "https://docs.all-hands.dev/",
        ["openrouter_app_name"] = "StayVibin",
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
        // Local-safe defaults: these cloud-only reasoning options are stripped by
        // NormalizeLocalLlm/SanitizeSavedSpec on every load anyway, so we write them
        // already-clean. Otherwise the freshly created file is briefly inconsistent
        // on disk and a stale agent-server reading the raw spec could pick up the
        // encrypted-reasoning path that makes local models return empty responses.
        ["enable_encrypted_reasoning"] = false,
        ["prompt_cache_retention"] = null,
        ["extended_thinking_budget"] = null,
        ["seed"] = null,
        ["usage_id"] = usageId,
        ["litellm_extra_body"] = new JsonObject()
    };

    /// <summary>Coerce a provider URL into the OpenAI-compatible '.../v1' form.</summary>
    private static string NormalizeOpenAiBaseUrl(string url)
    {
        var u = (url ?? "").Trim().TrimEnd('/');
        // Default to the bundled StayVibin Engine, never a separately-installed Ollama.
        if (u.Length == 0) u = StayVibinEngineManager.DefaultBaseUrl;
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
    /// <summary>
    /// max_size we stamp onto the condenser when the user disables automatic
    /// compaction. It is large enough that the condenser never trips on its own,
    /// while leaving manual compaction (the context ring) fully functional.
    /// </summary>
    public const int AutoCompactDisabledSize = 1_000_000;

    public static JsonNode Load(string? workingDir = null, string? workspaceSnapshot = null,
        string? path = null, PlanMode planMode = PlanMode.Off, bool autoCompact = true)
    {
        path ??= DefaultSettingsPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "No saved agent settings found.\n" +
                "Start StayVibin once and pick a model on first run, then retry.", path);

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)
            ?? throw new InvalidDataException("agent_settings.json is empty or invalid.");

        ApplyAccuracySettings(node);
        // When auto-compaction is turned off, push the condenser threshold so high it
        // never fires automatically; the user compacts manually via the context ring.
        if (!autoCompact && node["condenser"] is JsonObject cond)
            cond["max_size"] = AutoCompactDisabledSize;
        NormalizeLocalLlm(node);
        RemoveSubagentTools(node);
        AddSearchTools(node);
        InjectAgentContext(node, workingDir, workspaceSnapshot, planMode);
        return node;
    }

    /// <summary>
    /// Drop the delegation/subagent tool ("task_tool_set", surfaced as the "task"
    /// tool) from a saved spec. Local models cannot reliably drive subagents: the
    /// spawned subagent returns empty responses, OpenHands' stuck-detector trips, and
    /// the whole turn halts after the agent only said "let's start by..." - looking
    /// like it planned and then did nothing. Removing it keeps everything in the main
    /// agent, which works. task_tracker (the to-do list) is intentionally kept.
    /// </summary>
    private static void RemoveSubagentTools(JsonNode node)
    {
        if (node["tools"] is not JsonArray tools) return;

        for (int i = tools.Count - 1; i >= 0; i--)
        {
            var name = tools[i]?["name"]?.GetValue<string>();
            if (name is "task_tool_set" or "task")
                tools.RemoveAt(i);
        }
    }

    /// <summary>
    /// Strip cloud-only reasoning options from every LLM block before we send the
    /// spec to a LOCAL provider (Ollama). enable_encrypted_reasoning, the extended
    /// thinking budget, and prompt-cache retention are Anthropic/cloud features; on a
    /// local OpenAI-compatible endpoint they are at best ignored and at worst cause
    /// the SDK to round-trip reasoning in a shape the model returns empty for (the
    /// "LLM response contained no tool call and no content" loop). reasoning_effort is
    /// kept - Ollama maps it to its own think setting. Applies to the agent LLM and
    /// the condenser LLM.
    /// </summary>
    private static void NormalizeLocalLlm(JsonNode node)
    {
        NormalizeOneLlm(node["llm"] as JsonObject);
        NormalizeOneLlm((node["condenser"] as JsonObject)?["llm"] as JsonObject);

        static void NormalizeOneLlm(JsonObject? llm)
        {
            if (llm is null) return;
            llm["enable_encrypted_reasoning"] = false;
            llm["extended_thinking_budget"] = null;
            llm["prompt_cache_retention"] = null;

            // Migrate a legacy stock-Ollama endpoint (port 11434) to the bundled
            // StayVibin Engine so the app never relies on a separately-installed
            // Ollama. Preserves any custom remote endpoint the user set themselves.
            if (IsLegacyOllamaBaseUrl(llm["base_url"]?.GetValue<string>()))
                llm["base_url"] = StayVibinEngineManager.DefaultBaseUrl + "/v1";
        }
    }

    /// <summary>
    /// True when the URL points at a local stock-Ollama default (loopback:11434).
    /// Used to migrate older configs onto the bundled engine.
    /// </summary>
    private static bool IsLegacyOllamaBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var u)
               && (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                   || u.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
               && u.Port == 11434;
    }

    /// <summary>Same as <see cref="Load"/> but builds the workspace snapshot first.</summary>
    public static async Task<JsonNode> LoadAsync(
        string? workingDir = null, string? editorPath = null, string? path = null,
        PlanMode planMode = PlanMode.Off, bool autoCompact = true)
    {
        var snapshot = workingDir is null
            ? ""
            : await WorkspaceContextService.BuildAsync(workingDir, editorPath);
        return Load(workingDir, string.IsNullOrWhiteSpace(snapshot) ? null : snapshot, path, planMode, autoCompact);
    }

    /// <summary>
    /// Loosen condensation slightly so long investigations retain more early context
    /// (helps local models stay accurate on multi-step tasks).
    /// </summary>
    public static void ApplyAccuracySettings(JsonNode node)
    {
        if (node["condenser"] is not JsonObject cond) return;

        var keep = cond["keep_first"]?.GetValue<int>() ?? 2;
        if (keep < 6) cond["keep_first"] = 6;

        var max = cond["max_size"]?.GetValue<int>() ?? 240;
        if (max < 320) cond["max_size"] = 320;
    }

    public static string DescribeModel(JsonNode spec)
        => spec["llm"]?["model"]?.GetValue<string>() ?? "unknown model";

    /// <summary>
    /// Permanently clean a saved spec ON DISK: drop the subagent delegation tool and
    /// strip cloud-only reasoning options. <see cref="Load"/> already does this in
    /// memory, but older saved files keep <c>task_tool_set</c>, and the agent-server
    /// can outlive the UI - so a stale conversation (or any raw read) could resurrect
    /// the "delegate -> empty responses -> stuck" failure. Writing the cleaned spec
    /// back makes the fix stick regardless of code path. Returns true if it changed
    /// the file. Best-effort: never throws (startup must not fail over this).
    /// </summary>
    public static bool SanitizeSavedSpec(string? path = null)
    {
        try
        {
            path ??= DefaultSettingsPath();
            if (!File.Exists(path)) return false;

            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is null) return false;

            var before = node.ToJsonString();
            RemoveSubagentTools(node);
            NormalizeLocalLlm(node);
            if (node.ToJsonString() == before) return false;

            Save(node, path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read the saved agent spec unmodified (for the settings editor).</summary>
    public static JsonNode LoadRaw(string? path = null)
    {
        path ??= DefaultSettingsPath();
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "No saved agent settings found.\n" +
                "Start StayVibin once and pick a model on first run, then retry.", path);
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
        var modelField = obj["llm"]?["model"]?.GetValue<string>();
        parts.Add(PromptProfileForModel(modelField));

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
