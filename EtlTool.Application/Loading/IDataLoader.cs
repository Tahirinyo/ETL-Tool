using EtlTool.Application.Extraction;
using EtlTool.Application.MongoDB;

namespace EtlTool.Application.Loading;

public interface IDataLoader
{
    Task<BatchLoadResult> UpsertBatchAsync(
        IReadOnlyList<DataRow> rows,
        MongoTarget target,
        string upsertKeyField,
        CancellationToken cancellationToken);
}
