using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StayVibin.Models;

namespace StayVibin.Services;

/// <summary>A normalized, render-ready event derived from a raw server event.</summary>
public sealed record AgentUpdate(ChatRole Role, string Header, string Text, bool IsDelta);

/// <summary>Token/context usage snapshot from the server's stats stream.</summary>
public sealed record UsageStats(long TotalTokens, long PerTurnTokens, long ContextWindow, double Cost);

/// <summary>A persisted conversation as listed for the history sidebar.</summary>
public sealed record ConversationSummary(
    string Id, string Title, string WorkingDir, DateTime UpdatedAt, string Status,
    bool HasUserMessage);

/// <summary>Operator approval mode translated to the engine confirmation policy.</summary>
public enum AgentPermissionPolicy
{
    Ask,
    AllowAll
}

/// <summary>
/// Talks to StayVibin's local AI engine: creates conversations over REST and
/// streams events over a WebSocket. Raw server events are normalized into
/// <see cref="AgentUpdate"/> records the UI can render directly.
/// </summary>
public sealed class AgentServerClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Shared client for server-level (not conversation-bound) calls: listing and
    // deleting conversations for the history sidebar, which happen with no active
    // conversation. Static so it is reused rather than allocated per call.
    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;

    // ClientWebSocket forbids two overlapping SendAsync calls; Send and Steer can
    // both fire from the UI, so all sends are serialized through this gate.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string? ConversationId { get; private set; }

    public event Action<AgentUpdate>? Update;
    public event Action<string>? StatusChanged;
    public event Action<UsageStats>? StatsUpdated;
    public event Action? CompactingStarted;
    public event Action? Compacted;
    public event Action<string>? Disconnected;

    public AgentServerClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Create a new conversation from a serialized Agent spec (the same JSON the
    /// engine stores in ~/.openhands/agent_settings.json).
    /// </summary>
    public async Task<string> StartConversationAsync(
        JsonNode agentSpec, string workingDir, int maxIterations = 500,
        IReadOnlyDictionary<string, string>? toolModules = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["agent"] = agentSpec,
            ["workspace"] = new JsonObject
            {
                ["kind"] = "LocalWorkspace",
                ["working_dir"] = workingDir
            },
            ["max_iterations"] = maxIterations,
            ["stuck_detection"] = true
        };

        // Tell the server to dynamically import (register) any optional engine tools
        // the spec references but that are not registered at startup (grep, glob).
        // Without this the conversation create fails with "tool is not registered".
        if (toolModules is { Count: > 0 })
        {
            var map = new JsonObject();
            foreach (var kv in toolModules) map[kv.Key] = kv.Value;
            body["tool_module_qualnames"] = map;
        }

        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/conversations", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Create conversation failed ({(int)resp.StatusCode}): {text}");

        using var doc = JsonDocument.Parse(text);
        ConversationId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Server did not return a conversation id.");
        return ConversationId;
    }

    /// <summary>
    /// List persisted conversations (most recent first) for the history sidebar.
    /// Server-level call: works with no active conversation. Best-effort - returns
    /// an empty list on any error.
    /// </summary>
    public static async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        string baseUrl, CancellationToken ct = default)
    {
        var result = new List<ConversationSummary>();
        try
        {
            baseUrl = baseUrl.TrimEnd('/');
            using var resp = await _sharedHttp.GetAsync(
                $"{baseUrl}/api/conversations/search?limit=100&sort_order=CREATED_AT_DESC", ct);
            if (!resp.IsSuccessStatusCode) return result;

            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in items.EnumerateArray())
            {
                var id = GetString(item, "id");
                if (string.IsNullOrEmpty(id)) continue;

                var title = GetString(item, "title");
                var workingDir = "";
                if (item.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object)
                    workingDir = GetString(ws, "working_dir") ?? "";
                var status = GetString(item, "execution_status") ?? "";
                // last_user_message_id is set once the user sends a message, so a null
                // value marks an unused (empty) conversation we can hide from the list.
                var hasUserMessage = !string.IsNullOrEmpty(GetString(item, "last_user_message_id"));

                DateTime updated = DateTime.MinValue;
                var updatedRaw = GetString(item, "updated_at") ?? GetString(item, "created_at");
                if (!string.IsNullOrEmpty(updatedRaw)
                    && DateTime.TryParse(updatedRaw, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    updated = dt;

                result.Add(new ConversationSummary(
                    id!, title ?? "", workingDir, updated, status, hasUserMessage));
            }
        }
        catch { /* best-effort */ }
        return result;
    }

    /// <summary>Permanently delete a persisted conversation. Returns true on success.</summary>
    public static async Task<bool> DeleteConversationAsync(
        string baseUrl, string id, CancellationToken ct = default)
    {
        try
        {
            baseUrl = baseUrl.TrimEnd('/');
            using var resp = await _sharedHttp.DeleteAsync($"{baseUrl}/api/conversations/{id}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Bind this client to an existing persisted conversation (instead of creating a
    /// new one) so it can replay history and reconnect the event socket.
    /// </summary>
    public void AttachConversation(string id) => ConversationId = id;

    /// <summary>
    /// Replay a persisted conversation's events (oldest first) through the normal
    /// update pipeline so the chat UI is rebuilt exactly as it was. Call this BEFORE
    /// <see cref="ConnectAsync"/>; the event socket then streams only NEW events
    /// (default resend_mode=none), so nothing is rendered twice.
    /// </summary>
    public async Task ReplayHistoryAsync(CancellationToken ct = default)
    {
        if (ConversationId is null) return;

        string? pageId = null;
        do
        {
            var url = $"{_baseUrl}/api/conversations/{ConversationId}/events/search"
                      + "?limit=100&sort_order=TIMESTAMP";
            if (!string.IsNullOrEmpty(pageId))
                url += "&page_id=" + Uri.EscapeDataString(pageId!);

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;

            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    HandleRawEvent(item.GetRawText(), replay: true);

            pageId = GetString(root, "next_page_id");
        }
        while (!string.IsNullOrEmpty(pageId) && !ct.IsCancellationRequested);
    }

    /// <summary>Open the event WebSocket and begin streaming events in the background.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (ConversationId is null)
            throw new InvalidOperationException("Start a conversation before connecting.");

        var wsBase = _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
        var uri = new Uri($"{wsBase}/sockets/events/{ConversationId}");

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(uri, ct);

        _wsCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_wsCts.Token));
    }

    /// <summary>Send a user message; the server auto-runs the agent loop.</summary>
    public Task SendUserMessageAsync(string text, CancellationToken ct = default)
        => SendUserMessageAsync(text, null, ct);

    /// <summary>
    /// Send a user message, optionally with images (as data URLs) so vision-capable
    /// models can actually see them.
    /// </summary>
    public async Task SendUserMessageAsync(string text, IReadOnlyList<string>? imageUrls, CancellationToken ct = default)
    {
        if (_ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("WebSocket is not connected.");

        var content = new JsonArray();
        if (!string.IsNullOrEmpty(text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        if (imageUrls is not null)
            foreach (var url in imageUrls)
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = url }
                });

        var msg = new JsonObject { ["role"] = "user", ["content"] = content };
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());

        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Swap the conversation's LLM at runtime (model dropdown changes).</summary>
    public async Task SwitchLlmAsync(JsonNode llm, CancellationToken ct = default)
    {
        if (ConversationId is null)
            throw new InvalidOperationException("No active conversation to switch.");

        var body = new JsonObject { ["llm"] = llm };
        using var resp = await _http.PostAsJsonAsync(
            $"{_baseUrl}/api/conversations/{ConversationId}/switch_llm", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Switch model failed ({(int)resp.StatusCode}): {text}");
        }
    }

    /// <summary>
    /// Configure the engine's built-in confirmation policy. Ask confirms HIGH and
    /// UNKNOWN risk actions; AllowAll maps to NeverConfirm.
    /// </summary>
    public async Task SetConfirmationPolicyAsync(AgentPermissionPolicy policy, CancellationToken ct = default)
    {
        if (ConversationId is null)
            throw new InvalidOperationException("No active conversation to configure.");

        JsonObject policyNode = policy == AgentPermissionPolicy.AllowAll
            ? new JsonObject { ["kind"] = "NeverConfirm" }
            : new JsonObject
            {
                ["kind"] = "ConfirmRisky",
                ["threshold"] = "HIGH",
                ["confirm_unknown"] = true
            };

        var body = new JsonObject { ["policy"] = policyNode };
        using var resp = await _http.PostAsJsonAsync(
            $"{_baseUrl}/api/conversations/{ConversationId}/confirmation_policy", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Set permission policy failed ({(int)resp.StatusCode}): {text}");
        }
    }

    /// <summary>Accept or reject the pending confirmed action(s).</summary>
    public async Task RespondToConfirmationAsync(bool accept, string reason = "User rejected the action.",
        CancellationToken ct = default)
    {
        if (ConversationId is null)
            throw new InvalidOperationException("No active conversation to confirm.");

        var body = new JsonObject { ["accept"] = accept, ["reason"] = reason };
        using var resp = await _http.PostAsJsonAsync(
            $"{_baseUrl}/api/conversations/{ConversationId}/events/respond_to_confirmation", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Permission response failed ({(int)resp.StatusCode}): {text}");
        }
    }

    /// <summary>
    /// Pause a running conversation, stopping the agent's current task (it can be
    /// resumed later by sending another message). This is the server's "pause"
    /// endpoint - there is no "interrupt" route, so calling the wrong one 404s and
    /// the agent keeps running.
    /// </summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        if (ConversationId is null) return;
        try
        {
            using var resp = await _http.PostAsync(
                $"{_baseUrl}/api/conversations/{ConversationId}/pause", content: null, ct);
        }
        catch { /* best effort */ }
    }

    /// <summary>Force-summarize (compact) the conversation history now.</summary>
    public async Task CondenseAsync(CancellationToken ct = default)
    {
        if (ConversationId is null)
            throw new InvalidOperationException("No active conversation to compact.");
        using var resp = await _http.PostAsync(
            $"{_baseUrl}/api/conversations/{ConversationId}/condense", content: null, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Compact failed ({(int)resp.StatusCode}): {text}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Disconnected?.Invoke("Server closed the connection.");
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                HandleRawEvent(sb.ToString());
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Disconnected?.Invoke(ex.Message);
        }
    }

    private void HandleRawEvent(string json, bool replay = false)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;
            var kind = GetString(root, "kind") ?? "";

            switch (kind)
            {
                case "MessageEvent":
                {
                    var (role, text) = ReadMessage(root);
                    // Live: skip echoes of the user's own message (rendered locally).
                    // Replay: there is no local echo, so render the user message so the
                    // rebuilt transcript shows both sides of the conversation.
                    if (role == "user")
                    {
                        if (replay && !string.IsNullOrWhiteSpace(text))
                            Update?.Invoke(new AgentUpdate(ChatRole.User, "You", text.Trim(), false));
                        return;
                    }

                    // The model's reasoning can arrive either as a dedicated field
                    // (reasoning_content) or inlined into the message (<think> blocks,
                    // gpt-oss analysis channel). Surface it as a clean, separate
                    // "Thinking" block and keep only the real answer as the reply.
                    var (prose, inlineReasoning) = ChatText.SplitReasoning(text);
                    var reasoning = ReadReasoning(root);
                    if (string.IsNullOrWhiteSpace(reasoning)) reasoning = inlineReasoning;

                    if (!string.IsNullOrWhiteSpace(reasoning))
                        Update?.Invoke(new AgentUpdate(ChatRole.Thought, "Thinking", reasoning.Trim(), false));
                    if (!string.IsNullOrWhiteSpace(prose))
                        Update?.Invoke(new AgentUpdate(ChatRole.Agent, "Assistant", prose.Trim(), false));
                    break;
                }
                case "StreamingDeltaEvent":
                {
                    // During replay the final MessageEvent already carries the full
                    // text; replaying deltas too would duplicate it.
                    if (replay) break;
                    var delta = GetString(root, "content");
                    if (!string.IsNullOrEmpty(delta))
                        Update?.Invoke(new AgentUpdate(ChatRole.Agent, "Assistant", delta!, true));
                    break;
                }
                case "ActionEvent":
                {
                    var thought = ChatText.Strip(ExtractContentText(root, "thought"));
                    if (!string.IsNullOrWhiteSpace(thought))
                        Update?.Invoke(new AgentUpdate(ChatRole.Thought, "Thinking", thought.Trim(), false));

                    var tool = GetString(root, "tool_name");
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        var (role, header, detail) = DescribeAction(tool!, root);
                        // Always show the step header so the chat narrates every action,
                        // even when the action carries no extra detail.
                        Update?.Invoke(new AgentUpdate(role, header, detail, false));
                    }
                    break;
                }
                case "ObservationEvent":
                case "AgentErrorEvent":
                {
                    var text = SummarizeObservation(root);
                    var role = kind == "AgentErrorEvent" ? ChatRole.Error : ChatRole.Observation;
                    var header = kind == "AgentErrorEvent" ? "Error" : "Result";
                    if (!string.IsNullOrWhiteSpace(text))
                        Update?.Invoke(new AgentUpdate(role, header, text.Trim(), false));
                    break;
                }
                case "ConversationErrorEvent":
                case "ServerErrorEvent":
                {
                    var text = GetString(root, "detail") ?? GetString(root, "error") ?? "Unknown error";
                    Update?.Invoke(new AgentUpdate(ChatRole.Error, "Error", text, false));
                    break;
                }
                case "ConversationStateUpdateEvent":
                {
                    // These events carry many key/value pairs (full_state,
                    // last_user_message_id, execution_status, stats, ...).
                    var key = GetString(root, "key");
                    // During replay, skip execution_status: a persisted "running ->
                    // finished" transition would otherwise fire the live turn-finished
                    // logic (auto-continue, queue flush). Stats are still restored.
                    if (key == "execution_status" && !replay)
                    {
                        var status = GetString(root, "value");
                        if (!string.IsNullOrWhiteSpace(status))
                            StatusChanged?.Invoke(status!);
                    }
                    else if (key == "stats"
                             && root.TryGetProperty("value", out var sv)
                             && sv.ValueKind == JsonValueKind.Object)
                    {
                        HandleStats(sv);
                    }
                    else if (key == "full_state"
                             && root.TryGetProperty("value", out var fv)
                             && fv.ValueKind == JsonValueKind.Object
                             && fv.TryGetProperty("stats", out var fstats)
                             && fstats.ValueKind == JsonValueKind.Object)
                    {
                        HandleStats(fstats);
                    }
                    break;
                }
                case "CondensationRequest":
                    if (!replay) CompactingStarted?.Invoke();
                    break;
                case "Condensation":
                case "CondensationSummaryEvent":
                    if (!replay) Compacted?.Invoke();
                    break;
                default:
                    // Ignore token-level / log / housekeeping events.
                    break;
            }
        }
    }

    private void HandleStats(JsonElement stats)
    {
        if (!stats.TryGetProperty("usage_to_metrics", out var map)
            || map.ValueKind != JsonValueKind.Object)
            return;

        long total = 0, perTurn = 0, ctx = 0;
        double cost = 0;
        bool haveAgent = false;

        foreach (var entry in map.EnumerateObject())
        {
            var m = entry.Value;
            if (m.ValueKind != JsonValueKind.Object) continue;

            cost += GetDouble(m, "accumulated_cost");

            if (m.TryGetProperty("accumulated_token_usage", out var u)
                && u.ValueKind == JsonValueKind.Object)
            {
                total += GetLong(u, "prompt_tokens") + GetLong(u, "completion_tokens");

                var cw = GetLong(u, "context_window");
                var ptk = GetLong(u, "per_turn_token");

                // Prefer the main agent's context; fall back to the largest window.
                if (entry.NameEquals("agent"))
                {
                    perTurn = ptk;
                    ctx = cw;
                    haveAgent = true;
                }
                else if (!haveAgent && cw > ctx)
                {
                    perTurn = ptk;
                    ctx = cw;
                }
            }
        }

        StatsUpdated?.Invoke(new UsageStats(total, perTurn, ctx, cost));
    }

    // ---- JSON helpers -------------------------------------------------------

    private static long GetLong(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var n)
            ? n : 0;

    private static double GetDouble(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out var d)
            ? d : 0;

    private static string? GetString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static (string role, string text) ReadMessage(JsonElement root)
    {
        if (!root.TryGetProperty("llm_message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return ("", "");
        var role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() ?? "" : "";
        var text = ExtractContentText(msg, "content");
        return (role, text);
    }

    /// <summary>
    /// Read a model's reasoning when the runtime separates it into its own field
    /// (litellm/Ollama surface it as reasoning_content or reasoning on the message).
    /// Returns markup-free text, or "" if there is none. Inlined reasoning that the
    /// model wrote into the message body is handled separately by
    /// <see cref="ChatText.SplitReasoning"/>.
    /// </summary>
    private static string ReadReasoning(JsonElement root)
    {
        if (!root.TryGetProperty("llm_message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return "";
        var raw = GetString(msg, "reasoning_content") ?? GetString(msg, "reasoning");
        return string.IsNullOrWhiteSpace(raw) ? "" : ChatText.Strip(raw).Trim();
    }

    /// <summary>Join the text from a content array like [{"type":"text","text":"..."}].</summary>
    private static string ExtractContentText(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) { sb.Append(item.GetString()); continue; }
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var t)
                && t.ValueKind == JsonValueKind.String)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t.GetString());
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Turn a tool action into a friendly, step-by-step narration: a human header
    /// ("Run command", "Read file", "Edit file", "Create file", "Plan", ...) plus a
    /// concise detail (the command, the file path, a small preview of what changed).
    /// Falls back to a generic "Tool: name" summary for unknown tools.
    /// </summary>
    private static (ChatRole role, string header, string detail) DescribeAction(string tool, JsonElement root)
    {
        bool hasAction = root.TryGetProperty("action", out var action)
                         && action.ValueKind == JsonValueKind.Object;
        string? sub = hasAction ? GetString(action, "command") : null;   // file_editor sub-verb

        switch (tool.ToLowerInvariant())
        {
            // "terminal" plus the synonym aliases routed to it (see EngineExtensions).
            case "terminal":
            case "execute_bash":
            case "bash":
            case "shell":
            case "cmd":
            case "powershell":
            case "execute_powershell":
            case "run_command":
            {
                var cmd = hasAction ? GetString(action, "command") : null;
                return (ChatRole.Tool, "Run command",
                        string.IsNullOrWhiteSpace(cmd) ? "(no command)" : "$ " + Truncate(cmd!, 2000));
            }

            case "file_editor":
            case "str_replace_editor":
            {
                var path = hasAction ? GetString(action, "path") ?? "" : "";
                switch ((sub ?? "").ToLowerInvariant())
                {
                    case "view":
                        return (ChatRole.Tool, "Read file", path + ViewRange(action));
                    case "create":
                        return (ChatRole.Tool, "Create file",
                                Join(path, Preview(GetString(action, "file_text"))));
                    case "str_replace":
                        return (ChatRole.Tool, "Edit file",
                                Join(path, Preview(GetString(action, "new_str"))));
                    case "insert":
                    {
                        var line = hasAction ? GetLong(action, "insert_line") : 0;
                        return (ChatRole.Tool, "Edit file",
                                Join($"{path} (insert at line {line})", Preview(GetString(action, "new_str"))));
                    }
                    case "undo_edit":
                        return (ChatRole.Tool, "Undo edit", path);
                    default:
                        return (ChatRole.Tool, "Edit file", path);
                }
            }

            case "grep":
            case "search":   // alias -> grep
            {
                var pattern = hasAction ? GetString(action, "pattern") : null;
                var inc = hasAction ? GetString(action, "include") : null;
                var detail = string.IsNullOrWhiteSpace(pattern) ? "(no pattern)" : pattern!;
                if (!string.IsNullOrWhiteSpace(inc)) detail += $"  (in {inc})";
                return (ChatRole.Tool, "Search contents", detail);
            }

            case "glob":
            case "find":     // alias -> glob
            {
                var pattern = hasAction ? GetString(action, "pattern") : null;
                return (ChatRole.Tool, "Find files",
                        string.IsNullOrWhiteSpace(pattern) ? "(no pattern)" : pattern!);
            }

            case "task_tracker":
            case "task_tool_set":
                return (ChatRole.Tool, "Plan", DescribeTasks(action, hasAction, sub));

            case "think":
            case "thinktool":
            {
                var t = hasAction ? GetString(action, "thought") : null;
                return (ChatRole.Thought, "Thinking", t ?? "");
            }

            case "finish":
            case "finishtool":
            {
                var m = hasAction ? GetString(action, "message") : null;
                return (ChatRole.Tool, "Finished", m ?? "");
            }

            // Web browsing toolset (browser_use). Narrate the common actions.
            case "browser_navigate":
            {
                var url = hasAction ? GetString(action, "url") : null;
                return (ChatRole.Tool, "Browse",
                        string.IsNullOrWhiteSpace(url) ? "(navigate)" : "-> " + url!);
            }
            case "browser_get_content":
            case "browser_get_state":
                return (ChatRole.Tool, "Browse", "Read page content");
            case "browser_click":
            {
                var idx = hasAction ? GetLong(action, "index") : 0;
                return (ChatRole.Tool, "Browse", $"Click element #{idx}");
            }
            case "browser_type":
            {
                var t = hasAction ? GetString(action, "text") : null;
                return (ChatRole.Tool, "Browse",
                        string.IsNullOrWhiteSpace(t) ? "Type text" : "Type: " + Truncate(t!, 200));
            }
            case "browser_scroll":
                return (ChatRole.Tool, "Browse", "Scroll page");
            case "browser_go_back":
                return (ChatRole.Tool, "Browse", "Go back");
            case "browser_list_tabs":
            case "browser_switch_tab":
            case "browser_close_tab":
            case "browser_get_storage":
            case "browser_set_storage":
            case "browser_start_recording":
            case "browser_stop_recording":
                return (ChatRole.Tool, "Browse", tool!.Replace("browser_", "").Replace('_', ' '));

            default:
                return (ChatRole.Tool, $"Tool: {tool}", SummarizeActionFallback(root));
        }
    }

    /// <summary>Generic best-effort detail for an unrecognized tool.</summary>
    private static string SummarizeActionFallback(JsonElement root)
    {
        if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "command", "code", "path", "file_text", "query" })
            {
                var v = GetString(action, key);
                if (!string.IsNullOrWhiteSpace(v)) return Truncate(v!, 2000);
            }
        }
        return "";
    }

    /// <summary>Combine a one-line head with an optional indented preview block.</summary>
    private static string Join(string head, string preview)
        => string.IsNullOrEmpty(preview) ? head : head + "\n" + preview;

    /// <summary>Short, trimmed preview of file content being written.</summary>
    private static string Preview(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : Truncate(text!.Trim(), 400);

    /// <summary>Render a file_editor view_range like " (lines 10-40)" when present.</summary>
    private static string ViewRange(JsonElement action)
    {
        if (action.ValueKind == JsonValueKind.Object
            && action.TryGetProperty("view_range", out var r)
            && r.ValueKind == JsonValueKind.Array)
        {
            var nums = new List<int>();
            foreach (var e in r.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) nums.Add(n);
            if (nums.Count == 2) return $" (lines {nums[0]}-{nums[1]})";
        }
        return "";
    }

    /// <summary>Format the task_tracker plan as a checklist when tasks are present.</summary>
    private static string DescribeTasks(JsonElement action, bool hasAction, string? sub)
    {
        if (!hasAction) return sub ?? "";

        foreach (var key in new[] { "task_list", "tasks" })
        {
            if (!action.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            var sb = new StringBuilder();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title = GetString(item, "title") ?? GetString(item, "description")
                            ?? GetString(item, "task") ?? "";
                if (string.IsNullOrWhiteSpace(title)) continue;

                var mark = (GetString(item, "status") ?? "").ToLowerInvariant() switch
                {
                    "done" or "completed" => "[x]",
                    "in_progress" or "doing" or "active" => "[~]",
                    _ => "[ ]"
                };
                if (sb.Length > 0) sb.Append('\n');
                sb.Append($"{mark} {title}");
            }
            if (sb.Length > 0) return sb.ToString();
        }
        return string.IsNullOrWhiteSpace(sub) ? "Updated task list" : $"Task list: {sub}";
    }

    private static string SummarizeObservation(JsonElement root)
    {
        // Common shapes: content array, "output"/"text"/"observation" string.
        var text = ExtractContentText(root, "content");
        if (string.IsNullOrWhiteSpace(text))
        {
            foreach (var key in new[] { "output", "text", "observation", "detail", "error" })
            {
                var v = GetString(root, key);
                if (!string.IsNullOrWhiteSpace(v)) { text = v!; break; }
            }
        }
        return Truncate(text, 4000);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "\n... (truncated)";

    public void Dispose()
    {
        try { _wsCts?.Cancel(); } catch { }
        try
        {
            if (_ws is { State: WebSocketState.Open })
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                   .Wait(1000);
        }
        catch { }
        _ws?.Dispose();
        _wsCts?.Dispose();
        _sendLock.Dispose();
        _http.Dispose();
    }
}
