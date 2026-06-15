using System.Linq;

namespace StayVibin.Services;

/// <summary>
/// Human-friendly guidance for local models. This is intentionally conservative:
/// StayVibin is an agentic coding tool, so a model that is fine for chat may still
/// be too weak to use tools reliably.
/// </summary>
public static class ModelAdvisor
{
    /// <summary>
    /// Guidance about a selected model. Suggestions are real, tool-capable Ollama
    /// tags the UI can offer as one-click installs (no copy-paste needed).
    /// </summary>
    public sealed record FitnessNotice(string Severity, string Message, IReadOnlyList<string> Suggestions);

    /// <summary>
    /// One installable model for the Model Store.
    /// <para><b>Model</b> - the exact Ollama pull tag (must be a real library tag;
    /// cloud-only models like Claude/GPT/Gemini are intentionally excluded because
    /// they cannot be pulled locally).</para>
    /// <para><b>Tier</b> / <b>TierOrder</b> - display label and numeric sort key for
    /// the rough VRAM (Q4) the model wants. Going over a tier is fine: Ollama spills
    /// into system RAM and just runs slower.</para>
    /// <para><b>Category</b> - "Coding", "General", "Reasoning", or "Vision" for the
    /// store's category filter.</para>
    /// <para><b>Recommended</b> - true for picks we trust for StayVibin's agentic
    /// tool-calling workflow; these float to the top of their tier and get a badge.
    /// Non-recommended entries are still installable - freedom of choice - they are
    /// just not the ones we steer beginners toward.</para>
    /// </summary>
    public sealed record CatalogEntry(
        string Model, string Tier, int TierOrder, string Category, bool Recommended,
        AccuracyTier Accuracy, string Note);

    /// <summary>
    /// How accurate/grounded a local model is for real agentic coding, relative to
    /// what someone arriving from a paid frontier tool (Cursor, Claude, Codex) would
    /// expect. This is deliberately honest: NO local model matches a frontier cloud
    /// model today - the bigger local models just get closer. Accuracy tracks model
    /// size/quality, so it is derived from the VRAM tier (size proxy) and category.
    /// </summary>
    public enum AccuracyTier
    {
        ChatOnly,   // not suitable for agentic coding (too small, or vision-only)
        Basic,      // simple edits only; expect frequent mistakes
        Fair,       // okay for small tasks; needs supervision
        Good,       // solid for everyday vibe coding
        VeryGood,   // the closest local models get to a paid frontier tool
    }

    /// <summary>Short badge label for an accuracy tier (kept terse for the UI).</summary>
    public static string AccuracyLabel(AccuracyTier a) => a switch
    {
        AccuracyTier.VeryGood => "Top local accuracy",
        AccuracyTier.Good     => "Good accuracy",
        AccuracyTier.Fair     => "Fair accuracy",
        AccuracyTier.Basic    => "Basic accuracy",
        _                     => "Chat only",
    };

    /// <summary>App resource key for the accuracy badge color (shared by all rows).</summary>
    public static string AccuracyBrushKey(AccuracyTier a) => a switch
    {
        AccuracyTier.VeryGood => "Ok",
        AccuracyTier.Good     => "Ok",
        AccuracyTier.Fair     => "Warn",
        AccuracyTier.Basic    => "Warn",
        _                     => "Err",
    };

    /// <summary>
    /// Rate an already-installed model (arbitrary Ollama tag like
    /// "llama3.1:8b-instruct-fp16" or "qwen3.5:latest") so the Model Store can show
    /// the same accuracy/Recommended badges as the catalog. We first try to match the
    /// tag to a catalog entry by family+size; failing that we estimate accuracy from
    /// the model's parameter count (from /api/show or the tag name). Recommended is
    /// only claimed when it maps to a catalog model we actually recommend.
    /// </summary>
    public static (AccuracyTier Accuracy, bool Recommended) AssessInstalled(string model, ModelInfo? info)
    {
        var match = MatchCatalog(model);
        if (match is not null) return (match.Accuracy, match.Recommended);

        var b = ParameterBillions(model, info);
        var category = GuessCategory(model);
        return (AccuracyFor(BillionsToTier(b), category), false);
    }

    /// <summary>
    /// Best catalog entry for an installed tag: exact/":latest" match first, then a
    /// family+size match (so "llama3.1:8b-instruct-fp16" maps to "llama3.1:8b"),
    /// preferring a Recommended entry when several share a family and size.
    /// </summary>
    private static CatalogEntry? MatchCatalog(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var m = model.Trim();

        foreach (var e in Catalog)
            if (TagEquals(e.Model, m) || TagEquals(e.Model + ":latest", m) || TagEquals(e.Model, m + ":latest"))
                return e;

        return Catalog.Where(e => FamilySizeMatch(e.Model, m))
                      .OrderByDescending(e => e.Recommended)
                      .FirstOrDefault();
    }

    private static bool TagEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when two tags share a family (text before ':') and parameter size.</summary>
    private static bool FamilySizeMatch(string a, string b)
    {
        var sa = ParseBillions(a);
        if (sa <= 0 || Math.Abs(sa - ParseBillions(b)) > 0.001) return false;
        return string.Equals(Family(a), Family(b), StringComparison.OrdinalIgnoreCase);

        static string Family(string t)
        {
            var i = t.IndexOf(':');
            return i < 0 ? t : t[..i];
        }
    }

    /// <summary>Map a parameter count (billions) to one of the catalog VRAM tiers.</summary>
    private static int BillionsToTier(double b) => b switch
    {
        <= 0   => 8,   // unknown -> neutral "Fair" so we never over-promise
        < 2    => 2,
        < 4    => 4,
        < 7    => 6,
        < 10   => 8,
        < 14   => 12,
        < 20   => 16,
        < 28   => 24,
        < 40   => 32,
        < 90   => 48,
        _      => 80,
    };

    /// <summary>Rough category from a tag name (only Vision changes accuracy).</summary>
    private static string GuessCategory(string model)
    {
        var n = (model ?? "").ToLowerInvariant();
        if (n.Contains("vl") || n.Contains("vision") || n.Contains("llava")
            || n.Contains("moondream") || n.Contains("minicpm"))
            return "Vision";
        if (n.Contains("coder") || n.Contains("codestral") || n.Contains("codellama")
            || n.Contains("starcoder") || n.Contains("codegemma") || n.Contains("granite-code")
            || n.Contains("devstral") || n.Contains("codegeex") || n.Contains("opencoder")
            || n.Contains("stable-code") || n.Contains("sqlcoder") || n.Contains("codeqwen")
            || n.Contains("wizardcoder") || n.Contains("phind-codellama") || n.Contains("codebooga"))
            return "Coding";
        if (n.Contains("r1") || n.Contains("qwq") || n.Contains("reason")
            || n.Contains("think") || n.Contains("gpt-oss") || n.Contains("magistral")
            || n.Contains("marco-o1") || n.Contains("exaone-deep") || n.Contains("openthinker"))
            return "Reasoning";
        return "General";
    }

    // Families that advertise NATIVE tool-calling in Ollama (function calling). The
    // agent needs this to drive terminal/files/git, so it is the gate for being
    // "Recommended". Many strong CODE models (Codestral, CodeLlama, DeepSeek-Coder,
    // StarCoder) and REASONING models (DeepSeek-R1, QwQ) write great code but do NOT
    // expose tool-calling, so they are poor autonomous agents - we must not recommend
    // them even though they are smart. Matched by tag prefix.
    private static readonly string[] ToolFamilies =
    {
        "llama3.1", "llama3.2", "llama3.3", "llama4", "qwen3", "qwen2.5",
        "mistral-nemo", "mistral-small", "mistral-large", "mixtral", "devstral",
        "command-r", "command-a", "gpt-oss", "hermes3", "nemotron", "smollm2",
        // Newer families with documented native function-calling in Ollama.
        "granite3", "glm4", "cogito", "athene", "tulu3", "firefunction",
    };

    /// <summary>
    /// True when a tag's family is known to support native tool-calling in Ollama.
    /// Conservative on purpose: when unsure we return false so we never recommend a
    /// model the agent cannot actually drive. The runtime probe is the final word.
    /// </summary>
    private static bool ToolsLikely(string model)
    {
        var n = (model ?? "").ToLowerInvariant();
        if (n.Contains("qwen2.5-coder")) return false; // emits text tool calls; unreliable
        if (n == "mistral" || n.StartsWith("mistral:")) return true; // base Mistral supports tools
        foreach (var f in ToolFamilies)
            if (n.StartsWith(f)) return true;
        return false;
    }

    // Helper to keep the big catalog table below readable. Accuracy comes from the
    // size tier + category. Recommended is GATED by tool-calling support (a model
    // that cannot use tools is never recommended, regardless of the table), and
    // non-tool coders/reasoners get an honest note so the catalog never implies they
    // work as agents.
    private static CatalogEntry M(string model, int tier, string cat, bool rec, string note)
    {
        bool tools = ToolsLikely(model);
        if (!tools && (cat == "Coding" || cat == "Reasoning"))
            note += " No native tool-calling - codes/reasons well but will not drive the agent.";
        return new(model, TierLabel(tier), tier, cat, rec && tools, AccuracyFor(tier, cat), note);
    }

    /// <summary>
    /// Map a model's size tier + category to an honest accuracy expectation. Size is
    /// the dominant driver of grounded-coding accuracy, so we bucket by VRAM tier;
    /// vision models are flagged chat-only because they are not coding agents.
    /// </summary>
    private static AccuracyTier AccuracyFor(int tier, string category)
    {
        if (string.Equals(category, "Vision", StringComparison.OrdinalIgnoreCase))
            return AccuracyTier.ChatOnly;
        return tier switch
        {
            <= 2  => AccuracyTier.ChatOnly,
            4     => AccuracyTier.Basic,
            6     => AccuracyTier.Fair,
            8     => AccuracyTier.Fair,
            12    => AccuracyTier.Good,
            16    => AccuracyTier.Good,
            24    => AccuracyTier.VeryGood,
            32    => AccuracyTier.VeryGood,
            _     => AccuracyTier.VeryGood, // 48 GB+ dense/MoE: best local accuracy
        };
    }

    private static string TierLabel(int tier) => tier switch
    {
        <= 2  => "~2 GB VRAM",
        4     => "~4 GB VRAM",
        6     => "6 GB VRAM",
        8     => "8 GB VRAM",
        12    => "12 GB VRAM",
        16    => "16 GB VRAM",
        24    => "24 GB VRAM",
        32    => "32 GB VRAM",
        48    => "48 GB VRAM",
        80    => "80 GB+ VRAM",
        _     => "Multi-GPU / server",
    };

    /// <summary>
    /// One-click installable models shown in the Model Store. This is a broad list so
    /// operators have real choices, but every tag is a genuine Ollama library pull
    /// (cloud-only API models are excluded - they cannot run locally). Entries marked
    /// Recommended are the ones we trust for agentic coding; the rest are available
    /// for those who want them. The store sorts by tier (small to large), floats
    /// recommended picks to the top of each tier, and supports search / category /
    /// recommended-only filters. Operators can also install any tag by name.
    /// VRAM tiers are approximate Q4 footprints sourced from public model specs.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Catalog { get; } = new[]
    {
        // ---- ~2 GB: tiny; chat/experiments only, not for agent work --------------
        M("llama3.2:1b", 2, "General", false, "Tiny Llama 3.2; fast but too small for reliable tool use."),
        M("qwen3:0.6b", 2, "General", false, "Smallest Qwen3; experiments and quick demos only."),
        M("qwen3:1.7b", 2, "General", false, "Tiny Qwen3; quick chat, weak at tools."),
        M("qwen2.5:0.5b", 2, "General", false, "Tiny Qwen 2.5; experiments only."),
        M("qwen2.5:1.5b", 2, "General", false, "Small Qwen 2.5; light chat."),
        M("gemma3:1b", 2, "General", false, "Smallest Gemma 3; text-only, light chat. Weak tool-calling."),
        M("gemma2:2b", 2, "General", false, "Small Google chat model; weak tool-calling."),
        M("granite3.1-moe:1b", 2, "General", false, "IBM Granite MoE 1B; very fast, light tasks."),
        M("smollm2:1.7b", 2, "General", false, "Compact general model; surprisingly coherent for its size."),
        M("tinyllama", 2, "General", false, "1.1B; demos and very light chat only."),
        M("stablelm2:1.6b", 2, "General", false, "Small Stability general model."),
        M("deepseek-r1:1.5b", 2, "Reasoning", false, "Tiny distilled reasoner; shows thinking, but unreliable."),
        M("starcoder2:3b", 2, "Coding", false, "Small code-completion model (not agentic)."),

        // ---- ~4 GB: small ---------------------------------------------------------
        M("llama3.2:3b", 4, "General", false, "Small Llama 3.2; light chat and simple edits."),
        M("qwen3:4b", 4, "General", false, "Small Qwen3; tool use is fragile on real jobs."),
        M("qwen2.5:3b", 4, "General", false, "Small general Qwen 2.5."),
        M("gemma3:4b", 4, "General", false, "Gemma 3 4B; multimodal (sees images), good chat. Weak tools."),
        M("gemma3n:e4b", 4, "General", false, "Gemma 3n 4B; efficient on-device model. Weak tools."),
        M("phi3:mini", 4, "General", false, "Microsoft Phi-3 mini 3.8B; capable for its size."),
        M("phi3.5", 4, "General", false, "Updated Phi-3.5 mini 3.8B."),
        M("phi4-mini", 4, "General", false, "Phi-4 mini 3.8B; sharp reasoning, weak tool-calling."),
        M("granite3.1-dense:2b", 4, "General", false, "IBM Granite 3.1 dense 2B; advertises tool-calling."),
        M("nemotron-mini", 4, "General", false, "NVIDIA Nemotron-Mini 4B; tuned for tool use/roleplay."),
        M("exaone3.5:2.4b", 4, "General", false, "LG EXAONE 3.5 2.4B; bilingual general model."),
        M("falcon3:3b", 4, "General", false, "TII Falcon 3 small."),
        M("orca-mini:3b", 4, "General", false, "Classic small instruct model."),
        M("qwen2.5-coder:3b", 4, "Coding", false, "Small coder; autocomplete more than agentic."),
        M("stable-code:3b", 4, "Coding", false, "Stability code-completion 3B."),
        M("deepseek-coder:1.3b", 4, "Coding", false, "Tiny DeepSeek coder; completion only."),
        M("yi-coder:1.5b", 4, "Coding", false, "Small Yi coder."),
        M("opencoder:1.5b", 4, "Coding", false, "Small OpenCoder; completion."),
        M("codegemma:2b", 4, "Coding", false, "Small Google code model."),
        M("phi4-mini-reasoning", 4, "Reasoning", false, "Phi-4 mini tuned for math/step reasoning."),

        // ---- 8 GB: minimum practical agentic tier ---------------------------------
        M("qwen3:8b", 8, "General", true, "Great lightweight tool-caller; safe default at 8 GB."),
        M("llama3.1:8b", 8, "General", true, "Reliable Llama 3.1; strong all-rounder and tool-caller."),
        M("granite3.3:8b", 8, "General", true, "IBM Granite 3.3 8B; built for tool use and long context."),
        M("cogito:8b", 8, "General", true, "Deep Cogito 8B; hybrid reasoning + native tool use."),
        M("granite3.2:8b", 8, "General", false, "IBM Granite 3.2 8B; solid tool-calling."),
        M("glm4:9b", 8, "General", false, "Zhipu GLM-4 9B; capable, supports tool-calling."),
        M("tulu3:8b", 8, "General", false, "AI2 Tulu 3 8B; Llama-3.1 tune with tool support."),
        M("qwen2.5:7b", 8, "General", false, "Solid general Qwen 2.5."),
        M("mistral:7b", 8, "General", false, "Classic Mistral 7B; good chat, basic tool use."),
        M("hermes3:8b", 8, "General", false, "Llama-3.1 tuned for instruction following + tools."),
        M("command-r7b", 8, "General", false, "Cohere Command-R 7B; RAG/tool oriented, long context."),
        M("aya-expanse:8b", 8, "General", false, "Cohere multilingual 8B (23 languages)."),
        M("exaone3.5:7.8b", 8, "General", false, "LG EXAONE 3.5 8B; strong bilingual general model."),
        M("falcon3:7b", 8, "General", false, "TII Falcon 3 7B general model."),
        M("olmo2:7b", 8, "General", false, "AI2 OLMo 2 7B; fully open weights + data."),
        M("internlm2:7b", 8, "General", false, "InternLM2 7B general model."),
        M("yi:9b", 8, "General", false, "01.AI Yi 9B general model."),
        M("gemma2:9b", 8, "General", false, "Google 9B chat; weak tool-calling."),
        M("dolphin3:8b", 8, "General", false, "Uncensored Llama-3.1 8B tune."),
        M("zephyr", 8, "General", false, "Helpful Mistral chat tune."),
        M("openchat", 8, "General", false, "Strong open chat model (7B)."),
        M("starling-lm:7b", 8, "General", false, "RLAIF-tuned chat model."),
        M("neural-chat", 8, "General", false, "Intel chat tune (7B)."),
        M("wizardlm2:7b", 8, "General", false, "Microsoft WizardLM-2 7B chat."),
        M("nous-hermes2:10.7b", 8, "General", false, "Nous Hermes 2 SOLAR; instruction-tuned."),
        M("deepseek-r1:8b", 8, "Reasoning", false, "Distilled reasoner; thinks before acting."),
        M("deepseek-r1:7b", 8, "Reasoning", false, "Smaller distilled reasoner."),
        M("marco-o1", 8, "Reasoning", false, "Alibaba Marco-o1 7B open reasoning model."),
        M("exaone-deep:7.8b", 8, "Reasoning", false, "LG EXAONE Deep 8B reasoning model."),
        M("openthinker:7b", 8, "Reasoning", false, "Open reasoning model distilled from R1."),
        M("qwen2.5-coder:7b", 8, "Coding", false, "Coder, but often emits text tool calls - can stall."),
        M("deepseek-coder:6.7b", 8, "Coding", false, "DeepSeek coder 6.7B; strong code writing."),
        M("yi-coder:9b", 8, "Coding", false, "Yi coder 9B."),
        M("opencoder:8b", 8, "Coding", false, "OpenCoder 8B; reproducible code model."),
        M("codeqwen", 8, "Coding", false, "Qwen code-completion model (7B)."),
        M("codegeex4", 8, "Coding", false, "Zhipu CodeGeeX4 9B; multilingual code."),
        M("codellama:7b", 8, "Coding", false, "Meta CodeLlama 7B."),
        M("codegemma:7b", 8, "Coding", false, "Google code model 7B."),
        M("starcoder2:7b", 8, "Coding", false, "Code completion at 7B."),
        M("granite-code:8b", 8, "Coding", false, "IBM code model 8B."),
        M("dolphincoder", 8, "Coding", false, "StarCoder2-based uncensored coder (7B)."),
        M("mathstral", 8, "Coding", false, "Mistral tuned for math/STEM."),
        M("sqlcoder", 8, "Coding", false, "Defog model specialized for SQL generation."),

        // ---- 12 GB: first comfortable agentic tier --------------------------------
        M("qwen3:14b", 12, "General", true, "First truly capable tier for real file/terminal/git work."),
        M("mistral-nemo:12b", 12, "General", true, "12B with strong tool use; solid step up from 8B."),
        M("cogito:14b", 12, "General", true, "Deep Cogito 14B; reliable hybrid reasoning + tool use."),
        M("phi4", 12, "General", false, "Microsoft 14B; sharp, but tool-calling is unreliable in Ollama."),
        M("qwen2.5:14b", 12, "General", false, "Capable general Qwen 2.5 (14B)."),
        M("olmo2:13b", 12, "General", false, "AI2 OLMo 2 13B; fully open."),
        M("phi3:medium", 12, "General", false, "Phi-3 medium 14B."),
        M("solar", 12, "General", false, "Upstage SOLAR 10.7B general model."),
        M("solar-pro", 12, "General", false, "Upstage SOLAR Pro 22B (fits with spill); strong single-GPU."),
        M("vicuna:13b", 12, "General", false, "Classic 13B chat model."),
        M("llama2:13b", 12, "General", false, "Legacy Llama 2 13B; dated but stable."),
        M("nous-hermes2:34b", 12, "General", false, "Nous Hermes 2 (Yi-34B base); spills to RAM at 12 GB."),
        M("deepseek-r1:14b", 12, "Reasoning", false, "Mid distilled reasoner."),
        M("phi4-reasoning", 12, "Reasoning", false, "Phi-4 14B tuned for chain-of-thought reasoning."),
        M("deepseek-coder-v2:16b", 12, "Coding", false, "MoE coder; fast and good at writing code."),
        M("qwen2.5-coder:14b", 12, "Coding", false, "Coder 14B; can emit text tool calls."),
        M("starcoder2:15b", 12, "Coding", false, "Larger code-completion model."),

        // ---- 16 GB ----------------------------------------------------------------
        M("gpt-oss:20b", 16, "General", true, "Strong, reliable tool-caller (MXFP4 fits ~16 GB)."),
        M("codestral:22b", 16, "Coding", false, "Mistral's flagship code model; excellent at writing code."),
        M("internlm2:20b", 16, "General", false, "Larger InternLM2 (20B)."),

        // ---- 24 GB ----------------------------------------------------------------
        M("mistral-small3.2:24b", 24, "General", true, "Mistral Small 3.2 24B; improved instructions + function calling."),
        M("mistral-small3.1:24b", 24, "General", true, "Mistral Small 3.1 24B; multimodal + native tool-calling."),
        M("mistral-small:24b", 24, "General", true, "Mistral Small 24B; native function calling, great agent."),
        M("devstral:24b", 24, "Coding", true, "Mistral Devstral; purpose-built agentic coder - strong tool use."),
        M("qwen3:30b-a3b", 24, "General", true, "Fast Qwen3 MoE; great quality for its speed."),
        M("qwen3-coder:30b", 24, "Coding", true, "Coding-tuned MoE; excellent for vibe coding."),
        M("cogito:32b", 24, "General", true, "Deep Cogito 32B; strong hybrid reasoning + tool use."),
        M("magistral:24b", 24, "Reasoning", false, "Mistral Magistral 24B; transparent reasoning model."),
        M("deepseek-r1:32b", 24, "Reasoning", false, "Strong distilled reasoner for hard problems."),
        M("qwq", 24, "Reasoning", false, "Qwen QwQ reasoning model (32B)."),
        M("exaone-deep:32b", 24, "Reasoning", false, "LG EXAONE Deep 32B reasoning model."),
        M("qwen2.5:32b", 24, "General", false, "Dense 32B general Qwen."),
        M("qwen2.5-coder:32b", 24, "Coding", false, "Large coder; strong code, tool calls vary."),
        M("gemma2:27b", 24, "General", false, "Google 27B chat; weak tool-calling."),
        M("gemma3:27b", 24, "General", false, "Gemma 3 27B; strong multimodal chat. Weak tool-calling."),
        M("yi:34b", 24, "General", false, "01.AI Yi 34B general model."),
        M("aya-expanse:32b", 24, "General", false, "Cohere multilingual 32B (23 languages)."),
        M("command-r", 24, "General", false, "Cohere Command-R 35B; RAG/tool oriented."),
        M("codellama:34b", 24, "Coding", false, "Meta CodeLlama 34B."),
        M("deepseek-coder:33b", 24, "Coding", false, "DeepSeek 33B code model."),
        M("granite-code:34b", 24, "Coding", false, "IBM 34B code model."),
        M("phind-codellama", 24, "Coding", false, "Phind CodeLlama 34B; tuned for coding Q&A."),
        M("wizardcoder", 24, "Coding", false, "WizardCoder 33B; strong code generation."),

        // ---- 32 GB ----------------------------------------------------------------
        M("qwen3:32b", 32, "General", false, "Dense 32B with room for larger context."),
        M("mixtral:8x7b", 32, "General", false, "Mixtral 8x7B MoE; needs all experts resident."),
        M("dolphin-mixtral:8x7b", 32, "General", false, "Uncensored Mixtral 8x7B tune."),
        M("falcon:40b", 32, "General", false, "TII Falcon 40B general model."),

        // ---- 48 GB: high-end single/dual GPU --------------------------------------
        M("llama3.3:70b", 48, "General", true, "Latest Llama 70B; top open agentic quality."),
        M("llama3.1:70b", 48, "General", true, "Excellent 70B agentic model if you have the VRAM."),
        M("qwen2.5:72b", 48, "General", true, "Very strong 72B general/agentic model."),
        M("athene-v2:72b", 48, "General", true, "Nexusflow Athene-V2 72B (Qwen2.5 base); agent/tool tuned."),
        M("nemotron", 48, "General", true, "NVIDIA Nemotron 70B (Llama-3.1 tuned); strong tool use."),
        M("hermes3:70b", 48, "General", false, "70B instruction-tuned Llama with tools."),
        M("deepseek-r1:70b", 48, "Reasoning", false, "70B distilled reasoner."),
        M("dolphin-llama3:70b", 48, "General", false, "Uncensored Llama 3 70B tune."),

        // ---- 80 GB+: workstation / server -----------------------------------------
        M("gpt-oss:120b", 80, "General", true, "Top local tier; MXFP4 ~80 GB class, reliable tool-caller."),
        M("llama4:scout", 80, "General", true, "Llama 4 Scout MoE (109B/17B active); long context, tool-capable."),
        M("command-a", 80, "General", true, "Cohere Command A 111B; agentic/RAG flagship with tools."),
        M("qwen3-coder-next", 80, "Coding", true, "Qwen3 80B coder MoE; workstation tier."),
        M("command-r-plus", 80, "General", false, "Cohere Command-R+ 104B; RAG/tool oriented."),
        M("mistral-large", 80, "General", false, "Mistral Large 123B flagship; native function calling."),

        // ---- Multi-GPU / server: huge frontier-class models -----------------------
        M("llama4:maverick", 96, "General", false, "Llama 4 Maverick MoE (400B/17B active); server-class."),
        M("mixtral:8x22b", 96, "General", false, "Mixtral 8x22B (141B MoE); multi-GPU territory."),
        M("deepseek-v3", 96, "General", false, "DeepSeek V3 671B MoE; data-center scale."),
        M("deepseek-v2.5", 96, "General", false, "DeepSeek V2.5 236B MoE."),
        M("qwen3:235b-a22b", 96, "General", false, "Qwen3 235B MoE; server-class."),
        M("falcon:180b", 96, "General", false, "Falcon 180B; server-class."),
        M("deepseek-r1:671b", 96, "Reasoning", false, "Full DeepSeek R1; data-center scale."),

        // ---- Vision (multimodal): not agentic tool-callers, but available ----------
        M("moondream", 4, "Vision", false, "Tiny vision model for captions/QA (1.8B)."),
        M("granite3.2-vision:2b", 4, "Vision", false, "IBM Granite vision 2B; document/chart/OCR focus."),
        M("llava-phi3", 4, "Vision", false, "Compact LLaVA on Phi-3 (3.8B)."),
        M("qwen2.5vl:3b", 4, "Vision", false, "Qwen2.5-VL 3B vision-language."),
        M("llava:7b", 8, "Vision", false, "Open vision-language model (7B)."),
        M("llava-llama3", 8, "Vision", false, "LLaVA on Llama 3 (8B); improved vision."),
        M("bakllava", 8, "Vision", false, "BakLLaVA Mistral-based vision model."),
        M("minicpm-v", 8, "Vision", false, "Compact vision-language model (8B)."),
        M("qwen2.5vl:7b", 8, "Vision", false, "Qwen2.5-VL 7B; strong document/image understanding."),
        M("llama3.2-vision:11b", 12, "Vision", false, "Llama 3.2 Vision 11B; image understanding."),
        M("llava:13b", 12, "Vision", false, "Larger LLaVA vision model (13B)."),
        M("qwen2.5vl:32b", 24, "Vision", false, "Qwen2.5-VL 32B; high-end vision-language."),
        M("llama3.2-vision:90b", 48, "Vision", false, "Llama 3.2 Vision 90B; top open multimodal."),
        M("qwen2.5vl:72b", 48, "Vision", false, "Qwen2.5-VL 72B; flagship vision-language."),
    };

    /// <summary>Copy-paste install command for a model (public for the Model Store).</summary>
    public static string InstallCommandFor(string model) => InstallCommand(model);

    /// <summary>
    /// Full help text for the recommendations window, including copy-paste Ollama
    /// commands. This is intentionally written for operators, not model researchers.
    /// </summary>
    public static string RecommendationsWithCommands =>
        "The short version: use the strongest tool-capable model that fits fully in VRAM.\n"
        + "If a model chats fine but does not call tools reliably, it is not a good StayVibin model.\n"
        + "Picks below span several families (Qwen3, Llama 3.1, Mistral, gpt-oss) - qwen3 just\n"
        + "happens to be the most consistent tool-caller at small sizes. Gemma is left out on\n"
        + "purpose: it is a fine chat model but its tool-calling is weak for agent work.\n\n"
        + "4 GB VRAM\n"
        + "  Agentic coding is not recommended at this tier. Use a cloud/API model if possible.\n"
        + "  Experimental only:\n"
        + "  " + InstallCommand("qwen3:4b") + "\n\n"
        + "6 GB VRAM\n"
        + "  Tiny read-only tasks only. Expect tool mistakes on real vibe-coding work.\n"
        + "  On-par picks:\n"
        + "  " + InstallCommand("qwen3:4b") + "\n"
        + "  Step-up if it fits:\n"
        + "  " + InstallCommand("qwen3:8b") + "\n\n"
        + "8 GB VRAM\n"
        + "  Minimum experimental tier for StayVibin. Keep context modest.\n"
        + "  On-par picks:\n"
        + "  " + InstallCommand("qwen3:8b") + "\n"
        + "  " + InstallCommand("llama3.1:8b") + "\n"
        + "  Step-up if it fits:\n"
        + "  " + InstallCommand("qwen3:14b") + "\n\n"
        + "10 GB VRAM\n"
        + "  Similar to 8 GB, with a little more room for context.\n"
        + "  On-par picks:\n"
        + "  " + InstallCommand("qwen3:8b") + "\n"
        + "  " + InstallCommand("mistral-nemo:12b") + "\n"
        + "  Step-up if it fits:\n"
        + "  " + InstallCommand("qwen3:14b") + "\n\n"
        + "12 GB VRAM\n"
        + "  First practical StayVibin tier for real file/terminal/git work.\n"
        + "  On-par picks:\n"
        + "  " + InstallCommand("qwen3:14b") + "\n"
        + "  " + InstallCommand("mistral-nemo:12b") + "\n"
        + "  Step-up if it fits:\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n\n"
        + "16 GB VRAM\n"
        + "  Good mainstream tier. 14B has headroom; 20B may fit depending on quantization.\n"
        + "  Reliable picks:\n"
        + "  " + InstallCommand("qwen3:14b") + "\n"
        + "  Stronger pick if it fits:\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n\n"
        + "24 GB VRAM\n"
        + "  Sweet spot for local agentic coding.\n"
        + "  Recommended picks:\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n"
        + "  " + InstallCommand("mistral-small:24b") + "\n"
        + "  " + InstallCommand("qwen3:30b-a3b") + "\n"
        + "  " + InstallCommand("qwen3-coder:30b") + "\n\n"
        + "32 GB VRAM\n"
        + "  Strong single-GPU tier with room for larger context.\n"
        + "  Recommended picks:\n"
        + "  " + InstallCommand("qwen3-coder:30b") + "\n"
        + "  " + InstallCommand("qwen3:32b") + "\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n\n"
        + "48 GB+ VRAM\n"
        + "  Workstation tier. Use the largest model that stays fast enough for you.\n"
        + "  Recommended picks:\n"
        + "  " + InstallCommand("gpt-oss:120b") + "\n"
        + "  " + InstallCommand("qwen3-coder-next") + "\n\n"
        + "After installing a model, press the refresh button next to the model dropdown in StayVibin.";

    /// <summary>
    /// Explain whether the selected model is likely fit for StayVibin's agentic
    /// workflow. Returns null for models that look fine. Every notice includes an
    /// on-par (similar-VRAM) suggestion so the advice is actionable on the user's
    /// existing hardware, not just "go buy a bigger GPU".
    /// </summary>
    public static FitnessNotice? Assess(string model, ModelInfo? info)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var name = model.ToLowerInvariant();
        if (name.Contains("embed")) return null;

        var b = ParameterBillions(model, info);

        // Known-broken-for-tools family: advertises tools but emits text tool calls.
        if (name.Contains("qwen2.5-coder"))
            return new FitnessNotice(
                "not recommended",
                $"{model} often advertises tool support but returns its tool calls as plain "
                + "text, so the agent tends to stall or talk instead of acting. "
                + PeerSuggestion(b),
                PeerModels(b));

        if (!ModelTuning.SupportsTools(info))
            return new FitnessNotice(
                "not recommended",
                $"{model} does not advertise native tool support in Ollama, so it is unlikely "
                + "to drive files, terminal, and git reliably (it may still chat fine). "
                + PeerSuggestion(b),
                PeerModels(b));

        if (b is > 0 and < 8)
            return new FitnessNotice(
                "may struggle",
                $"{model} is on the small side for agent work - it may stop after promising "
                + "to do something, invent tool parameters, or hand the task back to you. "
                + PeerSuggestion(b),
                PeerModels(b));

        if (b is >= 8 and < 14)
            return new FitnessNotice(
                "borderline",
                $"{model} can handle simpler vibe tasks, but may talk instead of using tools "
                + "on bigger jobs. " + PeerSuggestion(b),
                PeerModels(b));

        return null;
    }

    /// <summary>
    /// Tool-capable models to suggest for a given parameter size: an on-par pick, a
    /// same-size alternative from a different family, and a more-reliable step-up.
    /// All are real Ollama tags spread across qwen3, llama3.1, mistral and gpt-oss so
    /// it is not all one brand. (Gemma is deliberately excluded: its tool-calling is
    /// weak for agent work.) Returned distinct, in install-priority order.
    /// </summary>
    public static IReadOnlyList<string> PeerModels(double billions)
    {
        var (peer, peerAlt, stepUp) = billions switch
        {
            > 0 and < 6    => ("qwen3:4b", "llama3.1:8b", "qwen3:8b"),
            >= 6 and < 10  => ("qwen3:8b", "llama3.1:8b", "qwen3:14b"),
            >= 10 and < 16 => ("qwen3:14b", "mistral-nemo:12b", "gpt-oss:20b"),
            >= 16 and < 24 => ("gpt-oss:20b", "mistral-small:24b", "qwen3:30b-a3b"),
            >= 24          => ("gpt-oss:20b", "qwen3-coder:30b", "qwen3:32b"),
            _              => ("qwen3:8b", "llama3.1:8b", "qwen3:14b")   // unknown size
        };
        return new[] { peer, peerAlt, stepUp }.Distinct().ToList();
    }

    /// <summary>
    /// Human-readable peer suggestion (no copy-paste commands - the UI offers
    /// one-click Install buttons for these models instead).
    /// </summary>
    private static string PeerSuggestion(double billions)
    {
        var picks = PeerModels(billions);
        var peer = picks.Count > 0 ? picks[0] : "qwen3:8b";
        var stepUp = picks.Count > 0 ? picks[^1] : "qwen3:14b";
        return $"A better-suited model at about the same VRAM is {peer}; if you can fit a "
               + $"little more, {stepUp} is noticeably more reliable. Use the Install "
               + "buttons below (or the Model Store) to add one in a click.";
    }

    /// <summary>
    /// Copy-paste shell command to install a model with the configured local
    /// provider. Only Ollama is supported today; future providers branch here.
    /// 'pull' downloads the model without dropping into an interactive chat, which
    /// is what we want before selecting it in StayVibin.
    /// </summary>
    private static string InstallCommand(string model) => $"ollama pull {model}";

    private static double ParameterBillions(string model, ModelInfo? info)
    {
        var fromInfo = ParseBillions(info?.ParameterSize);
        if (fromInfo > 0) return fromInfo;
        return ParseBillions(model);
    }

    private static double ParseBillions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.ToLowerInvariant();
        var idx = s.IndexOf('b');
        if (idx <= 0) return 0;

        int start = idx - 1;
        while (start >= 0 && (char.IsDigit(s[start]) || s[start] == '.'))
            start--;
        var token = s[(start + 1)..idx];
        return double.TryParse(token, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
