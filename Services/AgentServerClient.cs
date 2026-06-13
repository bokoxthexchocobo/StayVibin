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

/// <summary>
/// Talks to the OpenHands agent-server: creates conversations over REST and
/// streams events over a WebSocket. Raw server events are normalized into
/// <see cref="AgentUpdate"/> records the UI can render directly.
/// </summary>
public sealed class AgentServerClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;

    // ClientWebSocket forbids two overlapping SendAsync calls; Send and Steer can
    // both fire from the UI, so all sends are serialized through this gate.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string? ConversationId { get; private set; }

    public event Action<AgentUpdate>? Update;
    public event Action<string>? StatusChanged;
    public event Action<UsageStats>? StatsUpdated;
    public event Action? Compacted;
    public event Action<string>? Disconnected;

    public AgentServerClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Create a new conversation from a serialized Agent spec (the same JSON the
    /// OpenHands CLI persists in ~/.openhands/agent_settings.json).
    /// </summary>
    public async Task<string> StartConversationAsync(
        JsonNode agentSpec, string workingDir, int maxIterations = 500, CancellationToken ct = default)
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

        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/conversations", body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Create conversation failed ({(int)resp.StatusCode}): {text}");

        using var doc = JsonDocument.Parse(text);
        ConversationId = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Server did not return a conversation id.");
        return ConversationId;
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

    /// <summary>Interrupt a running conversation (cancels the in-flight LLM call).</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        if (ConversationId is null) return;
        try
        {
            using var resp = await _http.PostAsync(
                $"{_baseUrl}/api/conversations/{ConversationId}/interrupt", content: null, ct);
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

    private void HandleRawEvent(string json)
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
                    // Skip echoes of the user's own message (we render those locally).
                    if (role == "user") return;
                    if (!string.IsNullOrWhiteSpace(text))
                        Update?.Invoke(new AgentUpdate(ChatRole.Agent, "Assistant", text.Trim(), false));
                    break;
                }
                case "StreamingDeltaEvent":
                {
                    var delta = GetString(root, "content");
                    if (!string.IsNullOrEmpty(delta))
                        Update?.Invoke(new AgentUpdate(ChatRole.Agent, "Assistant", delta!, true));
                    break;
                }
                case "ActionEvent":
                {
                    var thought = ExtractContentText(root, "thought");
                    if (!string.IsNullOrWhiteSpace(thought))
                        Update?.Invoke(new AgentUpdate(ChatRole.Thought, "Thinking", thought.Trim(), false));

                    var tool = GetString(root, "tool_name");
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        var detail = SummarizeAction(root);
                        Update?.Invoke(new AgentUpdate(ChatRole.Tool, $"Tool: {tool}", detail, false));
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
                    if (key == "execution_status")
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
                case "Condensation":
                case "CondensationSummaryEvent":
                    Compacted?.Invoke();
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

    private static string SummarizeAction(JsonElement root)
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
