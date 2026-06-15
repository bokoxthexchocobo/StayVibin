using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StayVibin.Services;

/// <summary>
/// Metadata read from Ollama's /api/show. The architecture fields (layer/head
/// counts and head dimension) are optional - present when the model exposes them in
/// model_info - and are used to estimate KV-cache memory for VRAM-aware context
/// fitting. They default to 0 when unknown.
/// </summary>
public sealed record ModelInfo(
    long ContextLength, string Family, string ParameterSize, IReadOnlyList<string> Capabilities,
    int BlockCount = 0, int HeadCountKv = 0, int HeadDim = 0)
{
    /// <summary>
    /// Estimated KV-cache bytes consumed per context token, given the per-element
    /// byte cost of the cache (f16 ~ 2, q8_0 ~ 1, q4_0 ~ 0.5). Formula: 2 (K and V)
    /// * layers * KV heads * head dimension * element bytes. Returns 0 when the
    /// architecture metadata needed for the estimate is missing.
    /// </summary>
    public long KvBytesPerToken(double elementBytes)
        => BlockCount > 0 && HeadCountKv > 0 && HeadDim > 0
            ? (long)Math.Ceiling(2.0 * BlockCount * HeadCountKv * HeadDim * elementBytes)
            : 0;
}

/// <summary>An installed model and its on-disk size (from /api/tags).</summary>
public sealed record InstalledModel(string Name, long SizeBytes);

/// <summary>Streaming progress for a model download (from /api/pull).</summary>
public sealed record PullProgress(string Status, long Completed, long Total, double Percent);

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

    // Dedicated client for model downloads. A pull can stream for many minutes, so
    // it must not be subject to a fixed timeout; the caller's CancellationToken is
    // the only way it ends early. We read response headers first, then stream the
    // newline-delimited progress body without a deadline.
    private readonly HttpClient _pullHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

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

            // Scan model_info for the native context window plus the architecture
            // fields used to estimate KV-cache memory. Keys are namespaced by
            // architecture (e.g. "qwen2.block_count"), so we match on the suffix.
            long ctx = 0;
            int blockCount = 0, headCount = 0, headCountKv = 0, keyLength = 0, embeddingLength = 0;
            if (root.TryGetProperty("model_info", out var mi) && mi.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in mi.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Number) continue;
                    var key = p.Name;
                    if (ctx == 0 && key.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase))
                        ctx = p.Value.GetInt64();
                    else if (key.EndsWith(".block_count", StringComparison.OrdinalIgnoreCase))
                        blockCount = p.Value.GetInt32();
                    else if (key.EndsWith(".attention.head_count_kv", StringComparison.OrdinalIgnoreCase))
                        headCountKv = p.Value.GetInt32();
                    else if (key.EndsWith(".attention.head_count", StringComparison.OrdinalIgnoreCase))
                        headCount = p.Value.GetInt32();
                    else if (key.EndsWith(".attention.key_length", StringComparison.OrdinalIgnoreCase))
                        keyLength = p.Value.GetInt32();
                    else if (key.EndsWith(".embedding_length", StringComparison.OrdinalIgnoreCase))
                        embeddingLength = p.Value.GetInt32();
                }
            }

            // KV heads default to attention heads for non-GQA models. Head dimension
            // is the explicit key_length when present, else embedding_length / heads.
            int kvHeads = headCountKv > 0 ? headCountKv : headCount;
            int headDim = keyLength > 0
                ? keyLength
                : (headCount > 0 && embeddingLength > 0 ? embeddingLength / headCount : 0);

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

            var info = new ModelInfo(ctx, family, paramSize, caps, blockCount, kvHeads, headDim);
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
    /// plain-text JSON in message.content instead of message.tool_calls. The engine
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

    /// <summary>
    /// List installed models together with their on-disk size in bytes (from
    /// /api/tags). Used by the Model Store to show what is installed and how much
    /// space each model takes. Returns empty on failure.
    /// </summary>
    public async Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<InstalledModel>();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return Array.Empty<InstalledModel>();

            var list = new List<InstalledModel>();
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
                    continue;
                var name = n.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                long size = m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt64()
                    : 0;
                list.Add(new InstalledModel(name!, size));
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }
        catch
        {
            return Array.Empty<InstalledModel>();
        }
    }

    /// <summary>
    /// On-disk size in bytes of an installed model tag (from /api/tags), used to
    /// estimate how much VRAM/RAM the weights occupy once loaded. Tries an exact tag
    /// match, then ":latest", then a bare-name match. Returns 0 when the model is not
    /// installed or Ollama is unreachable.
    /// </summary>
    public async Task<long> GetModelDiskSizeAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return 0;
        var installed = await ListInstalledAsync(ct);
        if (installed.Count == 0) return 0;

        foreach (var m in installed)
            if (string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)) return m.SizeBytes;
        foreach (var m in installed)
            if (string.Equals(m.Name, model + ":latest", StringComparison.OrdinalIgnoreCase)) return m.SizeBytes;

        // Bare-name fallback: "llama3.1" matches an installed "llama3.1:8b".
        var bare = BareName(model);
        foreach (var m in installed)
            if (string.Equals(BareName(m.Name), bare, StringComparison.OrdinalIgnoreCase)) return m.SizeBytes;
        return 0;

        static string BareName(string tag)
        {
            int i = tag.IndexOf(':');
            return i < 0 ? tag : tag[..i];
        }
    }

    /// <summary>
    /// Download (install) a model via /api/pull, reporting streaming progress. The
    /// response is a stream of newline-delimited JSON status objects; we forward each
    /// as a <see cref="PullProgress"/>. Returns true if the pull reached "success".
    /// Throws on a reported error or transport failure so the caller can surface it.
    /// </summary>
    public async Task<bool> PullModelAsync(
        string model, IProgress<PullProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model name is required.", nameof(model));

        var json = $"{{\"model\":{JsonSerializer.Serialize(model)},\"stream\":true}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

        using var resp = await _pullHttp.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Install failed ({(int)resp.StatusCode}): {err}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        bool success = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            string status;
            long total = 0, completed = 0;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    throw new InvalidOperationException(e.GetString());

                status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? ""
                    : "";
                if (root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                    total = t.GetInt64();
                if (root.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.Number)
                    completed = c.GetInt64();
            }
            catch (JsonException)
            {
                continue;   // ignore a malformed progress line, keep streaming
            }

            double percent = total > 0 ? Math.Clamp(100.0 * completed / total, 0, 100) : 0;
            progress?.Report(new PullProgress(status, completed, total, percent));

            if (status.Equals("success", StringComparison.OrdinalIgnoreCase))
                success = true;
        }
        return success;
    }

    /// <summary>
    /// Remove (uninstall) a model via /api/delete. Returns true on success. DELETE
    /// with a body requires an explicit HttpRequestMessage.
    /// </summary>
    public async Task<bool> DeleteModelAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var json = $"{{\"model\":{JsonSerializer.Serialize(model)}}}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/delete")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        // Use the long-lived client (180s): removing a large model can touch many
        // blob files and occasionally exceeds the 4s metadata timeout on _http.
        using var resp = await _opHttp.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
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
        _pullHttp.Dispose();
    }
}
