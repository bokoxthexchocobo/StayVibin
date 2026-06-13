using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace StayVibin.Services;

/// <summary>Metadata read from Ollama's /api/show.</summary>
public sealed record ModelInfo(
    long ContextLength, string Family, string ParameterSize, IReadOnlyList<string> Capabilities);

/// <summary>
/// Read-only client for the local Ollama instance: lists installed models and
/// reads per-model metadata (context length, family, capabilities). Model
/// metadata is immutable for a given tag, so successful lookups are cached for the
/// app lifetime. Failures are NOT cached (they are usually transient - Ollama not
/// up yet), so a later retry or a Refresh re-queries them. Call
/// <see cref="ClearCache"/> to force a full re-fetch.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // Separate long-lived client for slow operations (tool-calling probe, warm-up)
    // that can take a minute+ while Ollama loads a model. Reused rather than created
    // per call to avoid socket churn / port exhaustion. Per-call deadlines are
    // applied with a linked CancellationTokenSource where a tighter bound is wanted.
    private readonly HttpClient _opHttp = new() { Timeout = TimeSpan.FromSeconds(180) };

    // Only successful lookups are stored; a missing entry means "not fetched yet".
    private readonly ConcurrentDictionary<string, ModelInfo> _infoCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Tool-calling probe results: true = the model emits structured tool_calls,
    // false = it only writes the call as plain text. Failed probes are not cached.
    private readonly ConcurrentDictionary<string, bool> _toolProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// True if the Ollama HTTP API is reachable (process running and serving). Hits
    /// the lightweight root endpoint and treats any HTTP response as "up"; only a
    /// connection failure/timeout counts as "down".
    /// </summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/version", ct);
            return true;   // any response means the server answered
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Return installed model names (e.g. "qwen2.5-coder:14b"), or empty on failure.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<string>();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var names = new List<string>();
            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    var name = n.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Fetch model metadata from Ollama's /api/show (context length, family,
    /// size, capabilities). Returns null if Ollama is unreachable or the model
    /// is unknown.
    /// </summary>
    public async Task<ModelInfo?> GetModelInfoAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (_infoCache.TryGetValue(model, out var cached)) return cached;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/show")
            {
                Content = new StringContent(
                    $"{{\"model\":{JsonSerializer.Serialize(model)}}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return null;   // transient/unknown - don't cache, allow a later retry

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            long ctx = 0;
            if (root.TryGetProperty("model_info", out var mi) && mi.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in mi.EnumerateObject())
                {
                    if (p.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.Number)
                    {
                        ctx = p.Value.GetInt64();
                        break;
                    }
                }
            }

            var caps = new List<string>();
            if (root.TryGetProperty("capabilities", out var ca) && ca.ValueKind == JsonValueKind.Array)
                foreach (var c in ca.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String) caps.Add(c.GetString()!);

            string family = "", paramSize = "";
            if (root.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("family", out var f) && f.ValueKind == JsonValueKind.String)
                    family = f.GetString() ?? "";
                if (d.TryGetProperty("parameter_size", out var ps) && ps.ValueKind == JsonValueKind.String)
                    paramSize = ps.GetString() ?? "";
            }

            var info = new ModelInfo(ctx, family, paramSize, caps);
            _infoCache[model] = info;
            return info;
        }
        catch
        {
            return null;   // transient (timeout/offline) - don't cache
        }
    }

    /// <summary>
    /// Probe whether a model returns STRUCTURED tool calls. Some Ollama models
    /// (notably qwen2.5-coder) advertise a "tools" capability but emit the call as
    /// plain-text JSON in message.content instead of message.tool_calls. OpenHands
    /// cannot act on that, so the agent silently "does nothing" and the raw JSON
    /// shows up as gibberish in chat. We send one tiny tools request and report
    /// whether the model put the call where it belongs. Result is cached per model.
    /// Returns null when the probe could not run (Ollama offline / timeout) so the
    /// caller can stay quiet rather than warn on a false negative.
    /// </summary>
    public async Task<bool?> ProbeToolCallingAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (_toolProbeCache.TryGetValue(model, out var cached)) return cached;

        // The model may have to load first. Allow up to 90s for this probe via a
        // linked token, on the shared long-lived client.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
        var pct = timeoutCts.Token;

        // Minimal request: one tool plus a prompt that should trigger it. We only
        // care about WHERE the call lands (tool_calls vs content), not the result.
        const string payload =
            "{\"model\":__MODEL__,\"stream\":false,\"options\":{\"temperature\":0}," +
            "\"messages\":[{\"role\":\"user\",\"content\":" +
            "\"List files in the current directory using the run_terminal tool.\"}]," +
            "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"run_terminal\"," +
            "\"description\":\"Run a shell command\",\"parameters\":{\"type\":\"object\"," +
            "\"properties\":{\"command\":{\"type\":\"string\"}},\"required\":[\"command\"]}}}]}";
        var json = payload.Replace("__MODEL__", JsonSerializer.Serialize(model));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await _opHttp.SendAsync(req, pct);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(pct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: pct);

            bool structured =
                doc.RootElement.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("tool_calls", out var tc)
                && tc.ValueKind == JsonValueKind.Array
                && tc.GetArrayLength() > 0;

            _toolProbeCache[model] = structured;
            return structured;
        }
        catch
        {
            return null;   // transient - don't cache, allow a later retry
        }
    }

    /// <summary>
    /// Pre-load a model into memory at a specific context size so the first real
    /// request does not pay the cold-load cost (which for a 14-20B model at a large
    /// num_ctx can be a minute or more). Loading at the SAME num_ctx the session
    /// will use avoids a second reload - Ollama re-loads the model when num_ctx
    /// changes. keep_alive holds it in memory between requests. Best-effort: any
    /// failure is swallowed since this is only an optimization.
    /// </summary>
    public async Task WarmAsync(string model, int numCtx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return;

        var ctx = numCtx >= 1 ? numCtx : 0;
        var options = ctx > 0 ? $",\"options\":{{\"num_ctx\":{ctx}}}" : "";

        // Empty prompt + keep_alive just loads the weights; it does not generate.
        var json = $"{{\"model\":{JsonSerializer.Serialize(model)},\"prompt\":\"\","
                   + $"\"stream\":false,\"keep_alive\":\"30m\"{options}}}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            using var resp = await _opHttp.SendAsync(req, ct);
        }
        catch { /* warm-up is best-effort */ }
    }

    /// <summary>Forget cached metadata so the next lookup re-queries Ollama (used by Refresh).</summary>
    public void ClearCache()
    {
        _infoCache.Clear();
        _toolProbeCache.Clear();
    }

    public void Dispose()
    {
        _http.Dispose();
        _opHttp.Dispose();
    }
}
