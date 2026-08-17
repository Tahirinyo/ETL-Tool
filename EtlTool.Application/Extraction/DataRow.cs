namespace EtlTool.Application.Extraction;

public sealed class DataRow
{
    /// <summary>
    /// Gets the one-based logical source-record position, including a header record when present.
    /// </summary>
    public long SourceRowNumber { get; init; }

    /// <summary>
    /// Gets the mutable logical field values for this row. Known missing values are represented by
    /// <see langword="null"/>, while explicitly empty text is represented by <see cref="string.Empty"/>.
    /// </summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
}
