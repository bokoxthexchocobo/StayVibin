namespace StayVibin.Services;

/// <summary>
/// Human-friendly guidance for local models. This is intentionally conservative:
/// StayVibin is an agentic coding tool, so a model that is fine for chat may still
/// be too weak to use tools reliably.
/// </summary>
public static class ModelAdvisor
{
    public sealed record FitnessNotice(string Severity, string Message);

    /// <summary>
    /// Curated starting points by VRAM tier. Tags are practical Ollama pulls where
    /// possible; availability can change, so the UI presents these as suggestions.
    /// </summary>
    public static string Recommendations =>
        "Recommended local agent models by VRAM:\n"
        + "- 4 GB: Not recommended for agentic coding. Use cloud/API, or expect chat-only behavior.\n"
        + "- 6 GB: qwen3:4b only for tiny read-only tasks; tool use will be fragile.\n"
        + "- 8 GB: qwen3:8b is the minimum experimental tier. Keep context small.\n"
        + "- 10 GB: qwen3:8b or a Q4 12B/14B model if it fits, but expect occasional tool mistakes.\n"
        + "- 12 GB: qwen3:14b is the first practical StayVibin tier.\n"
        + "- 16 GB: qwen3:14b with more headroom, or gpt-oss:20b if it fits your quantization.\n"
        + "- 24 GB: gpt-oss:20b, qwen3:30b-a3b, qwen3:32b, or qwen3-coder:30b.\n"
        + "- 32 GB: qwen3-coder:30b / qwen3:32b with larger context.\n"
        + "- 48 GB+: qwen3:72b, qwen3-coder-next, gpt-oss:120b class models.";

    /// <summary>
    /// Full help text for the recommendations window, including copy-paste Ollama
    /// commands. This is intentionally written for operators, not model researchers.
    /// </summary>
    public static string RecommendationsWithCommands =>
        "The short version: use the strongest tool-capable model that fits fully in VRAM.\n"
        + "If a model chats fine but does not call tools reliably, it is not a good StayVibin model.\n"
        + "Picks below span several families (Qwen3, Gemma 4, gpt-oss, Mistral) - qwen3 just\n"
        + "happens to be the most consistent tool-caller at small sizes.\n\n"
        + "4 GB VRAM\n"
        + "  Agentic coding is not recommended at this tier. Use a cloud/API model if possible.\n"
        + "  Experimental only:\n"
        + "  " + InstallCommand("qwen3:4b") + "\n"
        + "  " + InstallCommand("gemma4:e2b") + "\n\n"
        + "6 GB VRAM\n"
        + "  Tiny read-only tasks only. Expect tool mistakes on real vibe-coding work.\n"
        + "  On-par picks:\n"
        + "  " + InstallCommand("qwen3:4b") + "\n"
        + "  " + InstallCommand("gemma4:e2b") + "\n"
        + "  Step-up if it fits:\n"
        + "  " + InstallCommand("qwen3:8b") + "\n\n"
        + "8 GB VRAM\n"
        + "  Minimum experimental tier for StayVibin. Keep context modest.\n"
        + "  On-par picks:\n"
        + "  " + InstallCommand("qwen3:8b") + "\n"
        + "  " + InstallCommand("gemma4:e4b") + "\n"
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
        + "  " + InstallCommand("gemma4:e4b") + "\n"
        + "  Stronger pick if it fits:\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n\n"
        + "24 GB VRAM\n"
        + "  Sweet spot for local agentic coding.\n"
        + "  Recommended picks:\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n"
        + "  " + InstallCommand("gemma4:26b") + "\n"
        + "  " + InstallCommand("qwen3:30b-a3b") + "\n"
        + "  " + InstallCommand("qwen3-coder:30b") + "\n\n"
        + "32 GB VRAM\n"
        + "  Strong single-GPU tier with room for larger context.\n"
        + "  Recommended picks:\n"
        + "  " + InstallCommand("qwen3-coder:30b") + "\n"
        + "  " + InstallCommand("qwen3:32b") + "\n"
        + "  " + InstallCommand("gemma4:31b") + "\n"
        + "  " + InstallCommand("gpt-oss:20b") + "\n\n"
        + "48 GB+ VRAM\n"
        + "  Workstation tier. Use the largest model that stays fast enough for you.\n"
        + "  Recommended picks:\n"
        + "  " + InstallCommand("qwen3:72b") + "\n"
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
                + PeerSuggestion(b));

        if (!ModelTuning.SupportsTools(info))
            return new FitnessNotice(
                "not recommended",
                $"{model} does not advertise native tool support in Ollama, so it is unlikely "
                + "to drive files, terminal, and git reliably (it may still chat fine). "
                + PeerSuggestion(b));

        if (b is > 0 and < 8)
            return new FitnessNotice(
                "may struggle",
                $"{model} is on the small side for agent work - it may stop after promising "
                + "to do something, invent tool parameters, or hand the task back to you. "
                + PeerSuggestion(b));

        if (b is >= 8 and < 14)
            return new FitnessNotice(
                "borderline",
                $"{model} can handle simpler vibe tasks, but may talk instead of using tools "
                + "on bigger jobs. " + PeerSuggestion(b));

        return null;
    }

    /// <summary>
    /// Suggest a tool-capable model at roughly the same VRAM footprint (so it is a
    /// realistic swap on the same machine), plus an optional step-up that is more
    /// reliable if a bit more memory is available. Includes copy-paste install
    /// commands for the operator's local provider.
    /// </summary>
    private static string PeerSuggestion(double billions)
    {
        // (on-par pick, same-size alternative from a different family, more-reliable
        // step-up). All are tool-capable models that work well for agentic coding -
        // we spread across qwen3, gemma4, gpt-oss and mistral so it is not all one brand.
        var (peer, peerAlt, stepUp) = billions switch
        {
            > 0 and < 6    => ("qwen3:4b", "gemma4:e2b", "qwen3:8b"),
            >= 6 and < 10  => ("qwen3:8b", "gemma4:e4b", "qwen3:14b"),
            >= 10 and < 16 => ("qwen3:14b", "mistral-nemo:12b", "gpt-oss:20b"),
            >= 16 and < 24 => ("gpt-oss:20b", "gemma4:26b", "qwen3:30b-a3b"),
            >= 24          => ("gpt-oss:20b", "qwen3-coder:30b", "gemma4:31b"),
            _              => ("qwen3:8b", "gemma4:e4b", "qwen3:14b")   // unknown size
        };

        return $"For a similar-size model that handles vibe coding well, try {peer} or "
               + $"{peerAlt} (about the same VRAM). If you can fit a little more, {stepUp} "
               + "is noticeably more reliable.\n\nInstall (copy and paste):\n"
               + $"{InstallCommand(peer)}\n{InstallCommand(peerAlt)}\n{InstallCommand(stepUp)}";
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
