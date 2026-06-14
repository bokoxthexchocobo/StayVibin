namespace StayVibin.Services;

/// <summary>Recommended generation settings for a given model + its metadata.</summary>
public sealed record TuneResult(
    double Temperature, int ContextLength, string ReasoningEffort, bool NativeToolCalling);

/// <summary>
/// Picks sensible defaults so newbies never have to touch temperature/context.
/// Coding models run deterministic (temp 0) to stop them rambling, while all models
/// get high reasoning effort because the desktop app is used for agentic coding work.
/// Thinking/reasoning models (DeepSeek-R1, QwQ, Qwen3, Ollama "thinking" capability,
/// etc.) also get a little temperature headroom. Runtime context comes from the
/// user's Settings cap, or "auto" (the model's native window) when the cap is empty.
/// </summary>
public static class ModelTuning
{
    // Used for "auto" when no model window is known (matches AppSettings).
    public const int FallbackContextLength = 32768;
    public const int ContextFloor = 4096;

    // Ceiling for "auto" context. Modern models advertise huge native windows
    // (qwen3.5 reports 256K); allocating that as num_ctx forces Ollama to reserve a
    // massive KV cache, which makes the model take a minute+ to load and can swap or
    // OOM on 8-16GB machines. Auto therefore never exceeds this - power users who
    // really want more can set an explicit cap in Settings.
    public const int AutoContextCap = 32768;

    /// <param name="contextCap">
    /// Runtime context window (tokens) from Settings. A positive value is used as-is
    /// (explicit override - the user owns the memory trade-off). 0 means "auto": use
    /// the model's native window but clamped to <see cref="AutoContextCap"/> so a
    /// model with a 256K native window does not allocate a 256K KV cache by default.
    /// </param>
    public static TuneResult Recommend(string model, ModelInfo? info, int contextCap = 0)
    {
        var name = (model ?? "").ToLowerInvariant();

        bool isCoder = IsCodingModel(name);
        // Capability-first: any model Ollama marks as "thinking" gets reasoning tuning,
        // regardless of vendor (DeepSeek, Qwen, Phi, future releases).
        bool isThinking = !isCoder && IsThinkingModel(name, info);

        // Low temperatures favor accuracy and determinism, which is what an agentic
        // coding tool needs. Coders run fully deterministic. Reasoning models keep a
        // little headroom but well below a chat-style default: a high temperature on
        // a reasoning model mostly adds noise and hallucination on factual code
        // questions. gpt-oss does its reasoning in a dedicated channel, so a low
        // sampling temperature improves answer accuracy without hurting its
        // reasoning (and reduces the rambling that triggers empty-response loops).
        double temperature =
            isCoder ? 0.0
            : name.Contains("gpt-oss") ? 0.1
            : isThinking ? 0.3
            : 0.2;
        // Reasoning effort is model-aware. gpt-oss's harmony format treats "high"
        // as deep-math mode: it spends the whole generation in its analysis
        // channel and frequently ends a turn with NO final message and NO tool
        // call, which makes the agent loop on "LLM response contained no tool call
        // and no content". OpenAI's own guidance is low/medium reasoning for
        // agentic tool use, so gpt-oss gets "medium" to keep it acting instead of
        // over-thinking. Other reasoning models behave well with "high".
        string reasoning = name.Contains("gpt-oss") ? "medium" : "high";

        // Explicit cap wins as-is. Otherwise "auto" uses the model's native window,
        // clamped to AutoContextCap (and to the 32k fallback when native is unknown).
        long native = info?.ContextLength ?? 0;
        int ctx;
        if (contextCap > 0)
            ctx = contextCap;
        else if (native > 0)
            ctx = (int)Math.Min(native, AutoContextCap);
        else
            ctx = FallbackContextLength;
        if (ctx < ContextFloor) ctx = ContextFloor;

        return new TuneResult(temperature, ctx, reasoning, SupportsTools(info));
    }

    /// <summary>
    /// True when the model can use real (native) tool calling. Local models that
    /// support tools call them via Ollama's tool API; forcing the prompt-text
    /// fallback instead makes capable models emit malformed '&lt;function=...&gt;'
    /// markup, loop, and punt work back to the user, so we enable native calling
    /// whenever Ollama advertises the "tools" capability. When metadata is missing
    /// (Ollama unreachable) we assume native is fine - the ChatText cleaner still
    /// strips any stray markup as a safety net.
    /// </summary>
    public static bool SupportsTools(ModelInfo? info)
    {
        if (info?.Capabilities is not { } caps) return true;
        return caps.Any(c => c.Equals("tools", StringComparison.OrdinalIgnoreCase));
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
