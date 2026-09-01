using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;

namespace EtlTool.Infrastructure.Execution;

public sealed class RunSourceStore : IRunSourceStore
{
    private const string MissingExecutionConfigurationMessage =
        "The admitted ETL execution configuration is unavailable.";

    private readonly LocalRunSourceFileStore _fileSourceStore;
    private readonly IPostgreSqlConnectionFactory _postgreSqlConnectionFactory;
    private readonly IPostgreSqlMetadataDiscoveryService _postgreSqlMetadataDiscoveryService;
    private readonly PostgreSqlSourceSchemaConverter _postgreSqlSchemaConverter;
    private readonly SourceSchemaComparisonService _schemaComparisonService;
    private readonly PostgreSqlDeterministicOrderingResolver _postgreSqlOrderingResolver;

    public RunSourceStore(
        LocalRunSourceFileStore fileSourceStore,
        IPostgreSqlConnectionFactory postgreSqlConnectionFactory,
        IPostgreSqlMetadataDiscoveryService postgreSqlMetadataDiscoveryService,
        PostgreSqlSourceSchemaConverter postgreSqlSchemaConverter,
        SourceSchemaComparisonService schemaComparisonService,
        PostgreSqlDeterministicOrderingResolver postgreSqlOrderingResolver)
    {
        ArgumentNullException.ThrowIfNull(fileSourceStore);
        ArgumentNullException.ThrowIfNull(postgreSqlConnectionFactory);
        ArgumentNullException.ThrowIfNull(postgreSqlMetadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(postgreSqlSchemaConverter);
        ArgumentNullException.ThrowIfNull(schemaComparisonService);
        ArgumentNullException.ThrowIfNull(postgreSqlOrderingResolver);

        _fileSourceStore = fileSourceStore;
        _postgreSqlConnectionFactory = postgreSqlConnectionFactory;
        _postgreSqlMetadataDiscoveryService = postgreSqlMetadataDiscoveryService;
        _postgreSqlSchemaConverter = postgreSqlSchemaConverter;
        _schemaComparisonService = schemaComparisonService;
        _postgreSqlOrderingResolver = postgreSqlOrderingResolver;
    }

    public async Task<IEtlSource> OpenAsync(EtlRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = run.ExecutionConfiguration
            ?? throw new InvalidOperationException(MissingExecutionConfigurationMessage);

        return configuration.SourceType switch
        {
            SourceType.Csv or SourceType.Xlsx =>
                await _fileSourceStore.OpenAsync(run, cancellationToken).ConfigureAwait(false),
            SourceType.PostgreSql =>
                await OpenPostgreSqlAsync(configuration, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"The admitted source type '{configuration.SourceType}' is not supported for execution.")
        };
    }

    private async Task<IEtlSource> OpenPostgreSqlAsync(
        EtlRunExecutionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sourceOptions = configuration.PostgreSqlSource
            ?? throw new InvalidOperationException(
                "The admitted PostgreSQL source configuration is unavailable.");
        var columns = await _postgreSqlMetadataDiscoveryService.DiscoverColumnsAsync(
                sourceOptions.ConnectionProfile,
                sourceOptions.Database,
                sourceOptions.Schema,
                sourceOptions.Table,
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<SourceFieldDefinition> liveSchema;
        try
        {
            liveSchema = _postgreSqlSchemaConverter.Convert(columns);
        }
        catch (PostgreSqlUnsupportedColumnTypeException exception)
        {
            throw new PostgreSqlSourceSchemaChangedException(exception);
        }

        var comparison = _schemaComparisonService.Compare(
            configuration.ExpectedSchema,
            liveSchema,
            configuration.FieldMappings);
        if (comparison.HasDifferences || comparison.HasUnresolvedMappings)
        {
            throw new PostgreSqlSourceSchemaChangedException();
        }

        return new PostgreSqlEtlSource(
            _postgreSqlConnectionFactory,
            _postgreSqlMetadataDiscoveryService,
            _postgreSqlOrderingResolver,
            sourceOptions);
    }

    public Task ReleaseAsync(EtlRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        var configuration = run.ExecutionConfiguration;
        if (configuration is null)
        {
            // Runs created before execution snapshots existed were file-backed. The legacy
            // recovery path must still be able to remove their retained upload.
            return _fileSourceStore.ReleaseAsync(run, cancellationToken);
        }

        return configuration.SourceType switch
        {
            SourceType.Csv or SourceType.Xlsx =>
                _fileSourceStore.ReleaseAsync(run, cancellationToken),
            SourceType.PostgreSql => Task.CompletedTask,
            _ => throw new InvalidOperationException(
                $"The admitted source type '{configuration.SourceType}' is not supported for execution.")
        };
    }
}
