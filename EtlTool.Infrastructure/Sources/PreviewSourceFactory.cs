using EtlTool.Application.Extraction;
using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.Connections;

namespace EtlTool.Infrastructure.Sources;

public sealed class PreviewSourceFactory : IPreviewSourceFactory
{
    private readonly IWizardSourceStore _wizardSourceStore;
    private readonly IPostgreSqlConnectionFactory _postgreSqlConnectionFactory;
    private readonly IPostgreSqlMetadataDiscoveryService _postgreSqlMetadataDiscoveryService;
    private readonly PostgreSqlSourceSchemaConverter _postgreSqlSchemaConverter;
    private readonly PostgreSqlDeterministicOrderingResolver _postgreSqlOrderingResolver;
    private readonly MongoMetadataDatabase _mongoMetadataDatabase;
    private readonly MongoDbOptions _mongoDbOptions;
    private readonly IMongoSourceSchemaInferenceService _mongoSourceSchemaInferenceService;
    private readonly SourceSchemaComparisonService _schemaComparisonService;
    private readonly ISavedConnectionRevisionResolver? _savedConnectionRevisionResolver;
    private readonly ISavedConnectionRuntimeContextFactory? _savedConnectionRuntimeContextFactory;

    public PreviewSourceFactory(
        IWizardSourceStore wizardSourceStore,
        IPostgreSqlConnectionFactory postgreSqlConnectionFactory,
        IPostgreSqlMetadataDiscoveryService postgreSqlMetadataDiscoveryService,
        PostgreSqlSourceSchemaConverter postgreSqlSchemaConverter,
        PostgreSqlDeterministicOrderingResolver postgreSqlOrderingResolver,
        MongoMetadataDatabase mongoMetadataDatabase,
        MongoDbOptions mongoDbOptions,
        IMongoSourceSchemaInferenceService mongoSourceSchemaInferenceService,
        SourceSchemaComparisonService schemaComparisonService,
        ISavedConnectionRevisionResolver? savedConnectionRevisionResolver = null,
        ISavedConnectionRuntimeContextFactory? savedConnectionRuntimeContextFactory = null)
    {
        ArgumentNullException.ThrowIfNull(wizardSourceStore);
        ArgumentNullException.ThrowIfNull(postgreSqlConnectionFactory);
        ArgumentNullException.ThrowIfNull(postgreSqlMetadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(postgreSqlSchemaConverter);
        ArgumentNullException.ThrowIfNull(postgreSqlOrderingResolver);
        ArgumentNullException.ThrowIfNull(mongoMetadataDatabase);
        ArgumentNullException.ThrowIfNull(mongoDbOptions);
        ArgumentNullException.ThrowIfNull(mongoSourceSchemaInferenceService);
        ArgumentNullException.ThrowIfNull(schemaComparisonService);

        _wizardSourceStore = wizardSourceStore;
        _postgreSqlConnectionFactory = postgreSqlConnectionFactory;
        _postgreSqlMetadataDiscoveryService = postgreSqlMetadataDiscoveryService;
        _postgreSqlSchemaConverter = postgreSqlSchemaConverter;
        _postgreSqlOrderingResolver = postgreSqlOrderingResolver;
        _mongoMetadataDatabase = mongoMetadataDatabase;
        _mongoDbOptions = mongoDbOptions;
        _mongoSourceSchemaInferenceService = mongoSourceSchemaInferenceService;
        _schemaComparisonService = schemaComparisonService;
        _savedConnectionRevisionResolver = savedConnectionRevisionResolver;
        _savedConnectionRuntimeContextFactory = savedConnectionRuntimeContextFactory;
    }

    public async Task<IEtlSource?> AcquireAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        cancellationToken.ThrowIfCancellationRequested();

        return pipeline.SourceType switch
        {
            SourceType.Csv or SourceType.Xlsx => await AcquireFileSourceAsync(pipeline, cancellationToken).ConfigureAwait(false),
            SourceType.PostgreSql => await CreatePostgreSqlSourceAsync(pipeline, cancellationToken).ConfigureAwait(false),
            SourceType.MongoDb => await CreateMongoDbSourceAsync(pipeline, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"The pipeline source type '{pipeline.SourceType}' is not supported for preview.")
        };
    }

    private async Task<IEtlSource?> AcquireFileSourceAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken) =>
        await _wizardSourceStore.AcquireAsync(
                pipeline.Id,
                pipeline.SourceType,
                pipeline.SourceOptions,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<PostgreSqlEtlSource> CreatePostgreSqlSourceAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        var source = pipeline.PostgreSqlSource
            ?? throw new InvalidOperationException("The PostgreSQL source configuration is missing.");
        var connectionFactory = _postgreSqlConnectionFactory;
        var metadataDiscoveryService = _postgreSqlMetadataDiscoveryService;
        var effectiveSource = source;
        if (source.SavedConnectionId.HasValue)
        {
            var reference = await ResolveSavedConnectionAsync(
                    source.SavedConnectionId.Value,
                    DatabaseProviderType.PostgreSql,
                    cancellationToken)
                .ConfigureAwait(false);
            var context = await SavedRuntimeContextFactory()
                .CreatePostgreSqlAsync(reference.ConnectionId, reference.Revision, cancellationToken)
                .ConfigureAwait(false);
            connectionFactory = context.ConnectionFactory;
            metadataDiscoveryService = context.MetadataDiscovery;
            effectiveSource = new PostgreSqlSourceOptions
            {
                SavedConnectionId = reference.ConnectionId,
                SavedConnectionRevision = reference.Revision,
                ConnectionProfile = SavedConnectionProviderFactory.RuntimePostgreSqlProfile,
                Database = source.Database,
                Schema = source.Schema,
                Table = source.Table
            };
        }

        var columns = await metadataDiscoveryService.DiscoverColumnsAsync(
                effectiveSource.ConnectionProfile,
                effectiveSource.Database,
                effectiveSource.Schema,
                effectiveSource.Table,
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
            pipeline.ExpectedSchema,
            liveSchema,
            pipeline.FieldMappings);
        if (comparison.HasDifferences || comparison.HasUnresolvedMappings)
        {
            throw new PostgreSqlSourceSchemaChangedException();
        }

        return new PostgreSqlEtlSource(
            connectionFactory,
            metadataDiscoveryService,
            _postgreSqlOrderingResolver,
            effectiveSource);
    }

    private async Task<MongoDbEtlSource> CreateMongoDbSourceAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        var source = pipeline.MongoDbSource
            ?? throw new InvalidOperationException("The MongoDB source configuration is missing.");
        var metadataDatabase = _mongoMetadataDatabase;
        var mongoDbOptions = _mongoDbOptions;
        var schemaInferenceService = _mongoSourceSchemaInferenceService;
        if (source.SavedConnectionId.HasValue)
        {
            var reference = await ResolveSavedConnectionAsync(
                    source.SavedConnectionId.Value,
                    DatabaseProviderType.MongoDb,
                    cancellationToken)
                .ConfigureAwait(false);
            var context = await SavedRuntimeContextFactory()
                .CreateMongoDbAsync(reference.ConnectionId, reference.Revision, cancellationToken)
                .ConfigureAwait(false);
            metadataDatabase = context.MetadataDatabase;
            mongoDbOptions = context.Options;
            schemaInferenceService = context.SchemaInference;
        }

        IReadOnlyList<SourceFieldDefinition> liveSchema;
        try
        {
            liveSchema = await schemaInferenceService
                .InferAsync(source, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoSourceSchemaInferenceException exception)
        {
            throw new MongoSourceSchemaChangedException(exception);
        }
        var comparison = _schemaComparisonService.Compare(
            pipeline.ExpectedSchema,
            liveSchema,
            pipeline.FieldMappings);
        if (comparison.HasDifferences || comparison.HasUnresolvedMappings)
        {
            throw new MongoSourceSchemaChangedException();
        }

        return new MongoDbEtlSource(
            metadataDatabase,
            mongoDbOptions,
            source,
            pipeline.ExpectedSchema);
    }

    private async Task<SavedConnectionReference> ResolveSavedConnectionAsync(
        Guid connectionId,
        DatabaseProviderType providerType,
        CancellationToken cancellationToken)
    {
        var resolver = _savedConnectionRevisionResolver
            ?? throw new SavedConnectionResolutionException();
        return await resolver.ResolveCurrentAsync(connectionId, providerType, cancellationToken)
            .ConfigureAwait(false);
    }

    private ISavedConnectionRuntimeContextFactory SavedRuntimeContextFactory() =>
        _savedConnectionRuntimeContextFactory ?? throw new SavedConnectionResolutionException();
}
