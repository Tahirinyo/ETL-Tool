using EtlTool.Application.Execution;
using EtlTool.Application.Connections;
using EtlTool.Application.Extraction;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.Connections;

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
    private readonly MongoMetadataDatabase _mongoMetadataDatabase;
    private readonly IMongoSourceSchemaInferenceService _mongoSchemaInferenceService;
    private readonly MongoDbOptions _mongoDbOptions;
    private readonly ISavedConnectionRuntimeContextFactory? _savedConnectionRuntimeContextFactory;

    public RunSourceStore(
        LocalRunSourceFileStore fileSourceStore,
        IPostgreSqlConnectionFactory postgreSqlConnectionFactory,
        IPostgreSqlMetadataDiscoveryService postgreSqlMetadataDiscoveryService,
        PostgreSqlSourceSchemaConverter postgreSqlSchemaConverter,
        SourceSchemaComparisonService schemaComparisonService,
        PostgreSqlDeterministicOrderingResolver postgreSqlOrderingResolver,
        MongoMetadataDatabase mongoMetadataDatabase,
        IMongoSourceSchemaInferenceService mongoSchemaInferenceService,
        MongoDbOptions mongoDbOptions,
        ISavedConnectionRuntimeContextFactory? savedConnectionRuntimeContextFactory = null)
    {
        ArgumentNullException.ThrowIfNull(fileSourceStore);
        ArgumentNullException.ThrowIfNull(postgreSqlConnectionFactory);
        ArgumentNullException.ThrowIfNull(postgreSqlMetadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(postgreSqlSchemaConverter);
        ArgumentNullException.ThrowIfNull(schemaComparisonService);
        ArgumentNullException.ThrowIfNull(postgreSqlOrderingResolver);
        ArgumentNullException.ThrowIfNull(mongoMetadataDatabase);
        ArgumentNullException.ThrowIfNull(mongoSchemaInferenceService);
        ArgumentNullException.ThrowIfNull(mongoDbOptions);

        _fileSourceStore = fileSourceStore;
        _postgreSqlConnectionFactory = postgreSqlConnectionFactory;
        _postgreSqlMetadataDiscoveryService = postgreSqlMetadataDiscoveryService;
        _postgreSqlSchemaConverter = postgreSqlSchemaConverter;
        _schemaComparisonService = schemaComparisonService;
        _postgreSqlOrderingResolver = postgreSqlOrderingResolver;
        _mongoMetadataDatabase = mongoMetadataDatabase;
        _mongoSchemaInferenceService = mongoSchemaInferenceService;
        _mongoDbOptions = mongoDbOptions;
        _savedConnectionRuntimeContextFactory = savedConnectionRuntimeContextFactory;
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
            SourceType.MongoDb =>
                await OpenMongoDbAsync(configuration, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"The admitted source type '{configuration.SourceType}' is not supported for execution.")
        };
    }

    private async Task<IEtlSource> OpenMongoDbAsync(
        EtlRunExecutionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sourceOptions = configuration.MongoDbSource
            ?? throw new InvalidOperationException(
                "The admitted MongoDB source configuration is unavailable.");

        var metadataDatabase = _mongoMetadataDatabase;
        var schemaInferenceService = _mongoSchemaInferenceService;
        var mongoOptions = _mongoDbOptions;
        if (sourceOptions.SavedConnectionId.HasValue)
        {
            var reference = ResolveSavedSourceReference(
                configuration,
                sourceOptions.SavedConnectionId.Value,
                DatabaseProviderType.MongoDb);
            var runtimeFactory = _savedConnectionRuntimeContextFactory
                ?? throw new SavedConnectionResolutionException();
            var context = await runtimeFactory.CreateMongoDbAsync(
                    reference.ConnectionId,
                    reference.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            metadataDatabase = context.MetadataDatabase;
            schemaInferenceService = context.SchemaInference;
            mongoOptions = context.Options;
            sourceOptions = new MongoDbSourceOptions
            {
                SavedConnectionId = reference.ConnectionId,
                SavedConnectionRevision = reference.Revision,
                Database = sourceOptions.Database,
                Collection = sourceOptions.Collection
            };
        }
        else if (configuration.SourceConnection is not null)
        {
            throw new SavedConnectionResolutionException();
        }

        IReadOnlyList<SourceFieldDefinition> liveSchema;
        try
        {
            liveSchema = await schemaInferenceService
                .InferAsync(sourceOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoSourceSchemaInferenceException exception)
        {
            throw new MongoSourceSchemaChangedException(exception);
        }

        var comparison = _schemaComparisonService.Compare(
            configuration.ExpectedSchema,
            liveSchema,
            configuration.FieldMappings);
        if (comparison.HasDifferences || comparison.HasUnresolvedMappings)
        {
            throw new MongoSourceSchemaChangedException();
        }

        return new MongoDbEtlSource(
            metadataDatabase,
            mongoOptions,
            sourceOptions,
            configuration.ExpectedSchema);
    }

    private async Task<IEtlSource> OpenPostgreSqlAsync(
        EtlRunExecutionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sourceOptions = configuration.PostgreSqlSource
            ?? throw new InvalidOperationException(
                "The admitted PostgreSQL source configuration is unavailable.");
        var connectionFactory = _postgreSqlConnectionFactory;
        var metadataDiscoveryService = _postgreSqlMetadataDiscoveryService;
        var effectiveSourceOptions = sourceOptions;
        if (sourceOptions.SavedConnectionId.HasValue)
        {
            var reference = ResolveSavedSourceReference(
                configuration,
                sourceOptions.SavedConnectionId.Value,
                DatabaseProviderType.PostgreSql);
            var runtimeFactory = _savedConnectionRuntimeContextFactory
                ?? throw new SavedConnectionResolutionException();
            var context = await runtimeFactory.CreatePostgreSqlAsync(
                    reference.ConnectionId,
                    reference.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            connectionFactory = context.ConnectionFactory;
            metadataDiscoveryService = context.MetadataDiscovery;
            effectiveSourceOptions = new PostgreSqlSourceOptions
            {
                SavedConnectionId = reference.ConnectionId,
                SavedConnectionRevision = reference.Revision,
                ConnectionProfile = SavedConnectionProviderFactory.RuntimePostgreSqlProfile,
                Database = sourceOptions.Database,
                Schema = sourceOptions.Schema,
                Table = sourceOptions.Table
            };
        }
        else if (configuration.SourceConnection is not null)
        {
            throw new SavedConnectionResolutionException();
        }

        var columns = await metadataDiscoveryService.DiscoverColumnsAsync(
                effectiveSourceOptions.ConnectionProfile,
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
            connectionFactory,
            metadataDiscoveryService,
            _postgreSqlOrderingResolver,
            effectiveSourceOptions);
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
            SourceType.PostgreSql or SourceType.MongoDb => Task.CompletedTask,
            _ => throw new InvalidOperationException(
                $"The admitted source type '{configuration.SourceType}' is not supported for execution.")
        };
    }

    private static SavedConnectionReference ResolveSavedSourceReference(
        EtlRunExecutionConfiguration configuration,
        Guid sourceConnectionId,
        DatabaseProviderType expectedProviderType)
    {
        var reference = configuration.SourceConnection;
        if (reference is null
            || reference.ConnectionId != sourceConnectionId
            || reference.ProviderType != expectedProviderType
            || reference.Revision < 1)
        {
            throw new SavedConnectionResolutionException();
        }

        return reference;
    }
}
