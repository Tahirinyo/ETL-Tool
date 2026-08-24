using System.Collections.ObjectModel;
using EtlTool.Application.Processing;

namespace EtlTool.Application.Preview;

public sealed class PreviewResult
{
    public PreviewResult(IEnumerable<RowProcessingResult> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var copiedRows = rows.ToArray();
        if (copiedRows.Any(row => row is null))
        {
            throw new ArgumentException(
                "The preview row collection contains an invalid row.",
                nameof(rows));
        }

        Rows = new ReadOnlyCollection<RowProcessingResult>(copiedRows);
    }

    public IReadOnlyList<RowProcessingResult> Rows { get; }
}
