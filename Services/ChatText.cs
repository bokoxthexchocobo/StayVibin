using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StayVibin.Services;

/// <summary>
/// Cleans assistant text before it is shown in the chat. Local models driven with
/// non-native tool calling sometimes leak the raw prompt-format tool syntax into
/// their message content, e.g.:
///
///   &lt;function=terminal&gt;&lt;parameter=command&gt;pwd&lt;/parameter&gt;&lt;/function&gt;
///
/// That is plumbing, not something the user should see, so we remove those blocks.
/// The helper is also streaming-safe: while a block is still arriving (an opening
/// tag with no close yet) everything from the tag onward is hidden, so the bubble
/// shows only the real prose and never flashes a half-written tag.
/// </summary>
public static partial class ChatText
{
    // A complete <function=...>...</function> block (may contain nested parameters).
    [GeneratedRegex(@"<function=[\s\S]*?</function\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionBlock();

    // A complete standalone <parameter=...>...</parameter> block.
    [GeneratedRegex(@"<parameter=[\s\S]*?</parameter\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterBlock();

    // An unclosed tag still streaming in: drop from the tag to the end of the text.
    [GeneratedRegex(@"<(?:function|parameter)=[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingOpenTag();

    // gpt-oss / "harmony" control tokens, e.g. <|channel|>, <|message|>, <|start|>,
    // <|end|>, <|return|>, <|constrain|>. These are plumbing and must never show.
    [GeneratedRegex(@"<\|[^|>]*\|>", RegexOptions.IgnoreCase)]
    private static partial Regex HarmonyToken();

    // A closed reasoning block: <think>...</think> (qwen3, deepseek-r1, etc.).
    [GeneratedRegex(@"<think>([\s\S]*?)</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    // A reasoning block that has opened but not closed yet (defensive, non-streaming).
    [GeneratedRegex(@"<think>([\s\S]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenThinkBlock();

    // gpt-oss analysis channel content: ...analysis<|message|>REASONING<|...  We grab
    // the reasoning text up to the next control token (or end of string).
    [GeneratedRegex(@"analysis\s*<\|message\|>([\s\S]*?)(?:<\||$)", RegexOptions.IgnoreCase)]
    private static partial Regex AnalysisChannel();

    // The "message" (or synonym) field of a JSON reply envelope, capturing the
    // string value even when it is still streaming (no closing quote yet).
    [GeneratedRegex(
        "\"(?:message|response|answer|reply|output|content|text)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PartialEnvelopeMessage();

    /// <summary>
    /// Streaming counterpart to <see cref="UnwrapEnvelope"/>. Given the raw text so
    /// far, returns the partial answer when the model is mid-way through emitting a
    /// JSON reply envelope ({"message":"..."}), so the braces/keys never flash in the
    /// live bubble. Returns null when the text does not look like such an envelope
    /// (caller should append/clean normally).
    /// </summary>
    public static string? StreamingEnvelopeProse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var t = raw.TrimStart();
        if (t.Length == 0 || t[0] != '{') return null;

        var m = PartialEnvelopeMessage().Match(t);
        if (m.Success) return JsonUnescapeLenient(m.Groups[1].Value);

        // Object started and a message key is appearing but its value has not begun
        // yet: engage (show nothing) so the leading {"message":" never flashes.
        if (LooksLikeMessageKeyStart(t)) return "";
        return null;
    }

    // True if the (trimmed) object text has started a known message key. Tolerates a
    // key that is only partially streamed, e.g. just "{\"mess".
    private static bool LooksLikeMessageKeyStart(string t)
    {
        foreach (var key in EnvelopeMessageKeys)
        {
            var quoted = "\"" + key + "\"";
            if (t.IndexOf(quoted, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // Partial trailing key, e.g. t ends with "\"mess".
            int q = t.LastIndexOf('"');
            if (q >= 0)
            {
                var frag = t[(q + 1)..];
                if (frag.Length > 0 && key.StartsWith(frag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // Minimal JSON string unescape for a (possibly truncated) streamed fragment.
    private static string JsonUnescapeLenient(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (i + 1 >= s.Length) break;   // dangling backslash mid-stream: drop it
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'u':
                    if (i + 4 < s.Length &&
                        ushort.TryParse(s.Substring(i + 1, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    // Keys that carry the actual answer when a model wraps its reply in a JSON
    // object (some local models, when confused, emit the FinishTool/structured
    // shape as plain text, e.g. {"message":"...","summary":"..."}).
    private static readonly string[] EnvelopeMessageKeys =
        { "message", "response", "answer", "reply", "output", "content", "text" };

    // The full set of keys allowed in such an envelope. We only unwrap when EVERY
    // top-level key is one of these, so we never mangle real JSON the user actually
    // asked the model to produce (which would contain other, data-bearing keys).
    private static readonly HashSet<string> EnvelopeAllowedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "message", "response", "answer", "reply", "output", "content", "text",
            "summary", "title", "thought", "thoughts", "reasoning", "analysis",
            "status", "done", "finished", "next", "next_step", "plan",
        };

    /// <summary>
    /// If the whole text is a JSON object that only contains known "envelope" keys
    /// (a model leaking its structured reply shape as plain text), pull the answer
    /// out of it. Returns (answer, sideText) where sideText is any summary/thought
    /// fields that belong in the reasoning block. When the text is not such an
    /// envelope it is returned unchanged with an empty sideText.
    /// </summary>
    public static (string prose, string side) UnwrapEnvelope(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (text ?? "", "");

        var trimmed = text.Trim();
        // Cheap pre-check: must look like a single JSON object.
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
            return (text, "");

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (text, "");

            string? message = null;
            var side = new StringBuilder();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Any unknown key means this is real data, not a reply envelope.
                if (!EnvelopeAllowedKeys.Contains(prop.Name))
                    return (text, "");

                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var val = prop.Value.GetString() ?? "";

                if (message is null && IsMessageKey(prop.Name))
                    message = val;
                else if (val.Length > 0 &&
                         (prop.Name.Equals("summary", StringComparison.OrdinalIgnoreCase)
                          || prop.Name.Equals("thought", StringComparison.OrdinalIgnoreCase)
                          || prop.Name.Equals("thoughts", StringComparison.OrdinalIgnoreCase)
                          || prop.Name.Equals("reasoning", StringComparison.OrdinalIgnoreCase)
                          || prop.Name.Equals("analysis", StringComparison.OrdinalIgnoreCase)))
                {
                    if (side.Length > 0) side.Append('\n');
                    side.Append(val);
                }
            }

            // Only unwrap when we actually found an answer-bearing key; otherwise
            // leave the text alone rather than blanking the bubble.
            if (message is null) return (text, "");
            return (message, side.ToString());
        }
        catch
        {
            return (text, "");
        }

        static bool IsMessageKey(string name)
        {
            foreach (var k in EnvelopeMessageKeys)
                if (name.Equals(k, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Remove leaked tool-call markup from displayed assistant text.</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // Unwrap a JSON reply envelope first so the raw {"message":...} shape never
        // reaches the user; the extracted answer still flows through the markup
        // cleanup below in case it also contains tool/reasoning syntax.
        text = UnwrapEnvelope(text).prose;
        if (string.IsNullOrEmpty(text)) return "";

        bool hasToolMarkup =
            text.IndexOf("<function=", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("<parameter=", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasHarmony = text.IndexOf("<|", StringComparison.Ordinal) >= 0;

        // Fast path: nothing to do for the overwhelming majority of messages.
        if (!hasToolMarkup && !hasHarmony) return text;

        if (hasToolMarkup)
        {
            // Remove complete function blocks first (this also takes their nested
            // parameters), then any stray parameter blocks, then a trailing tag that
            // is still mid-stream.
            text = FunctionBlock().Replace(text, "");
            text = ParameterBlock().Replace(text, "");
            text = TrailingOpenTag().Replace(text, "");
        }

        // Drop any stray harmony control tokens so the raw channel syntax never shows.
        if (hasHarmony)
            text = HarmonyToken().Replace(text, "");

        return text.Trim();
    }

    /// <summary>
    /// Split assistant text into (prose, reasoning). Some local models inline their
    /// chain-of-thought into the message: qwen3 / deepseek-r1 use &lt;think&gt;...&lt;/think&gt;
    /// blocks, and gpt-oss emits an "analysis" harmony channel. We pull that out so
    /// it can be shown as a separate, clean "Thinking" block (Cursor-style) instead
    /// of appearing as the model's answer. Both returned parts are stripped of all
    /// markup/control-token syntax; either may be empty.
    /// </summary>
    public static (string prose, string reasoning) SplitReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ("", "");

        // Unwrap a JSON reply envelope up front. The answer becomes the prose; any
        // summary/thought fields become the seed of the reasoning block.
        var (unwrapped, side) = UnwrapEnvelope(text);
        text = unwrapped;

        bool hasThink = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasHarmony = text.IndexOf("<|", StringComparison.Ordinal) >= 0;
        if (!hasThink && !hasHarmony)
            return (Strip(text).Trim(), side.Trim());

        var reasoning = new StringBuilder();
        if (side.Length > 0) reasoning.Append(side.Trim());
        var prose = text;

        if (hasThink)
        {
            prose = ThinkBlock().Replace(prose, m =>
            {
                Append(reasoning, m.Groups[1].Value);
                return "";
            });
            prose = OpenThinkBlock().Replace(prose, m =>
            {
                Append(reasoning, m.Groups[1].Value);
                return "";
            });
        }

        if (hasHarmony)
        {
            // Capture the analysis channel text (must happen before tokens are stripped).
            foreach (Match m in AnalysisChannel().Matches(prose))
                Append(reasoning, m.Groups[1].Value);

            // Remove the analysis segment itself, then drop the leftover channel-name
            // words and any remaining control tokens from the visible prose.
            prose = AnalysisChannel().Replace(prose, "");
            prose = HarmonyToken().Replace(prose, "");
        }

        return (Strip(prose).Trim(), reasoning.ToString().Trim());

        static void Append(StringBuilder sb, string fragment)
        {
            var f = fragment.Trim();
            if (f.Length == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(f);
        }
    }
}
