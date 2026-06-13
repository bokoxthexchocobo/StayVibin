namespace StayVibin.Models;

/// <summary>
/// One row in the model dropdown. Embedding-only models are listed but disabled
/// (greyed) since they convert text to vectors and cannot chat; the reason is
/// surfaced on hover via <see cref="Tip"/>.
/// </summary>
public sealed class ModelEntry
{
    public required string Name { get; init; }
    public bool IsEmbedding { get; init; }

    /// <summary>Whether the model can be picked (false for embedding-only models).</summary>
    public bool IsSelectable => !IsEmbedding;

    /// <summary>Hover text; only set (non-null) for the disabled embedding models.</summary>
    public string? Tip => IsEmbedding
        ? "Embedding-only model - it turns text into vectors for search and cannot chat, "
          + "so it can't be used as an assistant here."
        : null;

    public override string ToString() => Name;
}
