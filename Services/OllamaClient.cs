using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace StayVibin.Services;

/// <summary>
/// Read-only client for the local Ollama instance: lists installed models and
/// reads per-model metadata (context length, family, capabilities). Model
/// metadata is immutable for a given tag, so it is cached for the app lifetime.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly ConcurrentDictionary<string, ModelInfo> _infoCache = new(StringComparer.OrdinalIgnoreCase);

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
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
                if (!resp.IsSuccessStatusCode) return null;

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
                return null;
            }
        }

        public void Dispose() => _http.Dispose();
    }

    /// <summary>Metadata read from Ollama's /api/show.</summary>
    public sealed record ModelInfo(
        long ContextLength, string Family, string ParameterSize, IReadOnlyList<string> Capabilities);
