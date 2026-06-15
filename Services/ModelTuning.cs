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
    private sealed record ModelProfile(
        string Match,
        double Temperature,
        string ReasoningEffort,
        int AutoContextCap,
        bool CompactPrompt,
        bool CompactToolset);

    // Used for "auto" when no model window is known (matches AppSettings).
    public const int FallbackContextLength = 32768;
    public const int ContextFloor = 4096;

    // Ceiling for "auto" context. Modern models advertise huge native windows
    // (qwen3.5 reports 256K); allocating that as num_ctx forces Ollama to reserve a
    // massive KV cache, which makes the model take a minute+ to load and can swap or
    // OOM on 8-16GB machines. Auto therefore never exceeds this - power users who
    // really want more can set an explicit cap in Settings.
    public const int AutoContextCap = 32768;

    private static readonly ModelProfile[] Profiles =
    [
        // Very small local models need the simplest, most disciplined runtime or
        // they waste turns paraphrasing the prompt instead of using tools.
        new("qwen3:1.7b", 0.10, "medium", 8192,  true,  true),
        new("qwen3:4b",   0.10, "medium", 12288, true,  true),
        new("qwen3:8b",   0.12, "medium", 16384, true,  true),
        new("llama3.1:8b",0.12, "medium", 16384, true,  true),
        new("llama3.2:3b",0.10, "medium", 12288, true,  true),
        new("mistral:7b", 0.12, "medium", 16384, true,  true),
        new("hermes3:8b", 0.12, "medium", 16384, true,  true),

        // Mid-tier models can keep more context and a fuller instruction set while
        // still benefiting from restrained sampling.
        new("qwen3:14b",        0.18, "high",   24576, false, false),
        new("mistral-nemo:12b", 0.18, "medium", 24576, false, false),
        new("gpt-oss:20b",      0.10, "medium", 24576, false, false),

        // Higher-end local models: keep deterministic coder behavior and moderate
        // sampling for general models while leaving enough context headroom.
        new("mistral-small:24b", 0.15, "medium", 32768, false, false),
        new("qwen3:30b-a3b",     0.15, "high",   32768, false, false),
        new("qwen3:32b",         0.15, "high",   32768, false, false),
        new("qwen3-coder:30b",   0.00, "medium", 32768, false, false),

        // Known-finicky coder family: keep it deterministic and constrained.
        new("qwen2.5-coder:3b",  0.00, "medium", 8192,  true,  true),
        new("qwen2.5-coder:7b",  0.00, "medium", 12288, true,  true),
        new("qwen2.5-coder:14b", 0.00, "medium", 16384, true,  true),
        new("qwen2.5-coder:32b", 0.00, "medium", 24576, false, false),
    ];

    // ---- VRAM-aware context fitting ------------------------------------------

    // Per-element KV-cache cost for each supported cache quantization. q8_0/q4_0
    // store small per-block scales alongside the quantized values, so the effective
    // byte cost sits a little above the raw 1.0 / 0.5; we round up to stay safe.
    private static double KvElementBytes(string? kvCacheType) =>
        (kvCacheType ?? "").Trim().ToLowerInvariant() switch
        {
            "q4_0" => 0.6,
            "q8_0" => 1.1,
            _      => 2.0,   // f16 (default / stable)
        };

    // VRAM reserved for the compute graph and activation buffers, on top of the
    // model weights and KV cache. Rough but conservative.
    private const long ComputeReserveBytes = 1L * 1024 * 1024 * 1024;   // ~1 GiB

    // Fraction of total VRAM we treat as usable (the rest covers the desktop,
    // driver, and other apps' allocations).
    private const double VramUsableFraction = 0.92;

    // Fraction of system RAM we treat as usable in CPU mode / as a VRAM fallback
    // (the rest must cover the OS, this app, the agent-server, and a browser).
    private const double RamUsableFraction = 0.60;

    // Minimum context we will auto-fit to, even if it slightly spills the KV cache
    // into system RAM: below this an agentic coding session is barely usable.
    public const int MinViableContext = 8192;

    // Never auto-fit above this regardless of native window or free memory.
    public const int MaxFitContext = 131072;

    // A "comfortable" working window for agentic coding. When the GPU's spare VRAM
    // alone gives at least this much, we keep the whole KV cache in VRAM for full
    // speed. When it gives less, we borrow system RAM (accepting some CPU offload) to
    // reach this target - so a big model on a single card gets a usable window
    // tailored to ALL the memory present, not just the VRAM gap left after its
    // weights. Above this, more context is not worth the speed cost unless the user
    // sets an explicit cap in Settings.
    public const int ComfortContext = 65536;

    /// <summary>
    /// Fit a model's runtime context window to the memory actually present so the
    /// weights AND the KV cache stay resident and the model still runs effectively.
    /// GPU mode keeps the KV cache in VRAM for full speed when the spare VRAM is
    /// already comfortable; otherwise it borrows a bounded slice of system RAM (the
    /// engine offloads the overflow layers to the CPU) to reach a usable window
    /// instead of cramming into the leftover VRAM. CPU mode (and the no-VRAM
    /// fallback) budgets off system RAM. Returns 0 when it cannot estimate (missing
    /// model architecture metadata or unknown memory) so the caller falls back to the
    /// static auto logic.
    /// </summary>
    /// <param name="info">Model metadata from /api/show (needs the architecture fields).</param>
    /// <param name="weightBytes">On-disk weight size, a proxy for loaded memory (0 = unknown).</param>
    /// <param name="device">GPU or CPU - selects the VRAM or RAM budget.</param>
    /// <param name="kvCacheType">KV cache quantization ("f16"/"q8_0"/"q4_0").</param>
    public static int FitContextToMemory(
        ModelInfo? info, long weightBytes, ComputeDevice device, string? kvCacheType)
    {
        if (info is null) return 0;

        long kvPerToken = info.KvBytesPerToken(KvElementBytes(kvCacheType));
        if (kvPerToken <= 0) return 0;   // no architecture info -> let caller fall back

        long ram = HardwareInfo.GetSystemRamBytes();
        long usableRam = ram > 0 ? (long)(ram * RamUsableFraction) : 0;

        // CPU mode (or no readable VRAM): the whole model + KV lives in system RAM.
        if (device == ComputeDevice.Cpu)
        {
            if (usableRam <= 0) return 0;
            return ClampFit(TokensForBudget(usableRam, weightBytes, kvPerToken));
        }

        long vram = HardwareInfo.GetVramBytes();
        if (vram <= 0)
        {
            if (usableRam <= 0) return 0;
            return ClampFit(TokensForBudget(usableRam, weightBytes, kvPerToken));
        }

        long usableVram = (long)(vram * VramUsableFraction);

        // Step 1: how big a window fits in VRAM alone (full GPU speed, no offload)?
        int vramFit = ClampFit(TokensForBudget(usableVram, weightBytes, kvPerToken));

        // If VRAM alone is already comfortable, keep everything in VRAM for speed.
        if (vramFit >= ComfortContext) return vramFit;

        // Step 2: VRAM is tight for this model - borrow system RAM. The engine fits
        // what it can in VRAM and offloads the overflow (weights + KV) to the CPU, so
        // the real budget is VRAM + usable RAM combined. Aim only as high as the
        // comfortable target (capped by the combined fit) so we gain working context
        // without dragging decode down chasing the full native window.
        int combinedFit = ClampFit(TokensForBudget(usableVram + usableRam, weightBytes, kvPerToken));
        int target = Math.Min(combinedFit, Math.Max(vramFit, ComfortContext));
        return ClampFit(target);
    }

    /// <summary>Token count whose KV cache fits in <paramref name="budgetBytes"/> after weights + reserve.</summary>
    private static long TokensForBudget(long budgetBytes, long weightBytes, long kvPerToken)
    {
        long available = budgetBytes - weightBytes - ComputeReserveBytes;
        return available <= 0 ? 0 : available / kvPerToken;
    }

    /// <summary>Clamp a raw token fit to [MinViableContext, MaxFitContext] and round to 2048.</summary>
    private static int ClampFit(long fit)
    {
        if (fit > MaxFitContext) fit = MaxFitContext;
        fit -= fit % 2048;                              // tidy multiple of 2048
        if (fit < MinViableContext) fit = MinViableContext;
        return (int)fit;
    }

    /// <param name="contextCap">
    /// Runtime context window (tokens) from Settings. A positive value is used as-is
    /// (explicit override - the user owns the memory trade-off). 0 means "auto".
    /// </param>
    /// <param name="autoFitCap">
    /// VRAM-aware fit from <see cref="FitContextToMemory"/> (0 when unavailable).
    /// Used for "auto" when &gt; 0, clamped to the model's native window, so memory
    /// rather than a flat constant decides the window. When 0, auto falls back to the
    /// native window clamped to <see cref="AutoContextCap"/>.
    /// </param>
    public static TuneResult Recommend(string model, ModelInfo? info, int contextCap = 0, int autoFitCap = 0)
    {
        var name = (model ?? "").ToLowerInvariant();
        var profile = MatchProfile(name);

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
        double temperature = profile?.Temperature
            ?? (isCoder ? 0.0
                : name.Contains("gpt-oss") ? 0.1
                : isThinking ? 0.3
                : 0.2);
        // Reasoning effort is model-aware. gpt-oss's harmony format treats "high"
        // as deep-math mode: it spends the whole generation in its analysis
        // channel and frequently ends a turn with NO final message and NO tool
        // call, which makes the agent loop on "LLM response contained no tool call
        // and no content". OpenAI's own guidance is low/medium reasoning for
        // agentic tool use, so gpt-oss gets "medium" to keep it acting instead of
        // over-thinking. Other reasoning models behave well with "high".
        string reasoning = profile?.ReasoningEffort
            ?? (name.Contains("gpt-oss") ? "medium" : "high");

        // Context selection priority:
        //  1. Explicit user cap (Settings) - used as-is; the user owns the trade-off.
        //  2. VRAM-aware fit - the window that keeps weights + KV resident in memory,
        //     clamped to the model's native window.
        //  3. Static auto - native window clamped to AutoContextCap (used when the fit
        //     could not be computed, e.g. missing architecture/memory info).
        //  4. 32k fallback when even the native window is unknown.
        long native = info?.ContextLength ?? 0;
        int ctx;
        if (contextCap > 0)
            ctx = contextCap;
        else if (autoFitCap > 0)
            ctx = native > 0 ? (int)Math.Min(native, autoFitCap) : autoFitCap;
        else if (native > 0)
            ctx = (int)Math.Min(native, profile?.AutoContextCap ?? AutoContextCap);
        else
            ctx = FallbackContextLength;
        if (ctx < ContextFloor) ctx = ContextFloor;

        return new TuneResult(temperature, ctx, reasoning, SupportsTools(info));
    }

    public static bool UseCompactPromptProfile(string model, ModelInfo? info = null)
    {
        var profile = MatchProfile((model ?? "").ToLowerInvariant());
        if (profile is not null) return profile.CompactPrompt;
        var b = ParameterBillions(model ?? "", info);
        return b > 0 && b <= 8.5;
    }

    public static bool UseCompactToolset(string model, ModelInfo? info = null)
    {
        var profile = MatchProfile((model ?? "").ToLowerInvariant());
        if (profile is not null) return profile.CompactToolset;
        var b = ParameterBillions(model ?? "", info);
        return b > 0 && b <= 8.5;
    }

    private static ModelProfile? MatchProfile(string name)
        => Profiles
            .OrderByDescending(p => p.Match.Length)
            .FirstOrDefault(p =>
                name.Equals(p.Match, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(p.Match + ":", StringComparison.OrdinalIgnoreCase));

    private static double ParameterBillions(string model, ModelInfo? info)
    {
        var source = !string.IsNullOrWhiteSpace(info?.ParameterSize) ? info!.ParameterSize : model ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(
            source.ToLowerInvariant(),
            @"(\d+(?:\.\d+)?)\s*b");
        return match.Success
            && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;
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
