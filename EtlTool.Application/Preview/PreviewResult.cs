using System.Collections.ObjectModel;
using EtlTool.Application.Processing;

namespace EtlTool.Application.Preview;

public sealed class PreviewResult
{
    public PreviewResult(IEnumerable<RowProcessingResult> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var copiedRows = new List<RowProcessingResult>();

        foreach (var row in rows)
        {
            if (row is null)
            {
                throw new ArgumentException(
                    "The preview row collection contains an invalid row.",
                    nameof(rows));
            }

            copiedRows.Add(row);

            switch (row.Status)
            {
                case RowProcessingStatus.Valid:
                    ValidRowCount++;
                    break;
                case RowProcessingStatus.Invalid:
                    InvalidRowCount++;
                    break;
                case RowProcessingStatus.Filtered:
                    FilteredRowCount++;
                    break;
            }
        }

        Rows = new ReadOnlyCollection<RowProcessingResult>(copiedRows);
    }

    public IReadOnlyList<RowProcessingResult> Rows { get; }

    public int ValidRowCount { get; }

    public int InvalidRowCount { get; }

    public int FilteredRowCount { get; }
}
