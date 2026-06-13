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

    /// <summary>Remove leaked tool-call markup from displayed assistant text.</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // Fast path: nothing to do for the overwhelming majority of messages.
        if (text.IndexOf("<function=", StringComparison.OrdinalIgnoreCase) < 0
            && text.IndexOf("<parameter=", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        // Remove complete function blocks first (this also takes their nested
        // parameters), then any stray parameter blocks, then a trailing tag that
        // is still mid-stream.
        text = FunctionBlock().Replace(text, "");
        text = ParameterBlock().Replace(text, "");
        text = TrailingOpenTag().Replace(text, "");
        return text.Trim();
    }
}
