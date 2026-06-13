namespace StayVibin.Services;

/// <summary>Recommended generation settings for a given model + its metadata.</summary>
public sealed record TuneResult(double Temperature, int ContextLength, string ReasoningEffort);

/// <summary>
/// Picks sensible defaults so newbies never have to touch temperature/context.
/// Coding models run deterministic (temp 0) to stop them rambling and lecturing;
/// reasoning models get a little creativity and full reasoning effort. Context
/// is taken from the model's real window, capped to stay light on local RAM.
/// </summary>
public static class ModelTuning
{
    // Safe ceiling for local machines; raise in Settings if you have the RAM.
    public const int ContextCap = 32768;
    public const int ContextFloor = 4096;

    public static TuneResult Recommend(string model, ModelInfo? info)
    {
        var name = (model ?? "").ToLowerInvariant();

        bool isCoder =
            name.Contains("coder") || name.Contains("code") ||
            name.Contains("starcoder") || name.Contains("codestral") ||
            name.Contains("codellama") || name.Contains("deepseek-coder");

        bool capThinking = info?.Capabilities is { } caps &&
            caps.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase));

        bool isThinking = capThinking ||
            name.Contains("r1") || name.Contains("qwq") || name.Contains("magistral") ||
            name.Contains("thinking") || name.Contains("gpt-oss") ||
            name.StartsWith("qwen3") || name.StartsWith("qwen/qwen3");

        double temperature = isCoder ? 0.0 : (isThinking ? 0.6 : 0.2);
        string reasoning = isThinking ? "high" : "low";

        long native = info?.ContextLength ?? 0;
        int ctx = native > 0 ? (int)Math.Min(native, ContextCap) : ContextCap;
        if (ctx < ContextFloor) ctx = ContextFloor;

        return new TuneResult(temperature, ctx, reasoning);
    }
}
