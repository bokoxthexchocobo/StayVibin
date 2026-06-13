namespace StayVibin.Services;

/// <summary>Recommended generation settings for a given model + its metadata.</summary>
public sealed record TuneResult(double Temperature, int ContextLength, string ReasoningEffort);

/// <summary>
/// Picks sensible defaults so newbies never have to touch temperature/context.
/// Coding models run deterministic (temp 0) to stop them rambling; thinking/reasoning
/// models (DeepSeek-R1, QwQ, Qwen3, Ollama "thinking" capability, etc.) get higher
/// reasoning effort and a little temperature headroom. Context adapts to the model's
/// real window, capped at a user-controlled ceiling to stay light on local RAM.
/// </summary>
public static class ModelTuning
{
    // Default ceiling when the caller doesn't supply one (matches AppSettings).
    public const int DefaultContextCap = 65536;
    public const int ContextFloor = 4096;

    /// <param name="contextCap">
    /// Upper bound on the runtime context window (tokens). The chosen context is
    /// min(model's native window, this cap), so smaller models use less and larger
    /// models are clamped here. Defaults to <see cref="DefaultContextCap"/>.
    /// </param>
    public static TuneResult Recommend(string model, ModelInfo? info, int contextCap = DefaultContextCap)
    {
        var name = (model ?? "").ToLowerInvariant();

        bool isCoder = IsCodingModel(name);
        // Capability-first: any model Ollama marks as "thinking" gets reasoning tuning,
        // regardless of vendor (DeepSeek, Qwen, Phi, future releases).
        bool isThinking = !isCoder && IsThinkingModel(name, info);

        double temperature = isCoder ? 0.0 : isThinking ? 0.55 : 0.2;
        string reasoning = isThinking ? "high" : "low";

        // Guard against a nonsensical cap, then fit the model's native window into it.
        int cap = contextCap > 0 ? contextCap : DefaultContextCap;
        long native = info?.ContextLength ?? 0;
        int ctx = native > 0 ? (int)Math.Min(native, cap) : cap;
        if (ctx < ContextFloor) ctx = ContextFloor;

        return new TuneResult(temperature, ctx, reasoning);
    }

    /// <summary>True for instruct/coder variants that should stay deterministic.</summary>
    private static bool IsCodingModel(string name)
        => name.Contains("coder") || name.Contains("codestral")
           || name.Contains("starcoder") || name.Contains("codellama")
           || name.Contains("deepseek-coder") || name.Contains("codegemma")
           || name.Contains("granite-code") || name.Contains("codeqwen");

    /// <summary>
    /// True for chain-of-thought / reasoning models. Prefers Ollama's capability
    /// flag; falls back to well-known name patterns when metadata is missing.
    /// </summary>
    public static bool IsThinkingModel(string name, ModelInfo? info)
    {
        if (info?.Capabilities is { } caps &&
            caps.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Name heuristics when /api/show does not report capabilities yet.
        return name.Contains("-r1") || name.Contains("/r1")
               || name.Contains("deepseek-r1") || name.Contains("deepseek-reasoner")
               || name.Contains("qwq") || name.Contains("magistral")
               || name.Contains("reasoning") || name.Contains("think")
               || name.Contains("gpt-oss")
               || name.StartsWith("qwen3") || name.StartsWith("qwen/qwen3")
               || name.Contains("o1") || name.Contains("o3")
               || name.Contains("phi4-reasoning");
    }
}
