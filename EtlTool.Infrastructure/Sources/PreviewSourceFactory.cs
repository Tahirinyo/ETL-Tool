using EtlTool.Application.Extraction;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.MongoDB;

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

    public PreviewSourceFactory(
        IWizardSourceStore wizardSourceStore,
        IPostgreSqlConnectionFactory postgreSqlConnectionFactory,
        IPostgreSqlMetadataDiscoveryService postgreSqlMetadataDiscoveryService,
        PostgreSqlSourceSchemaConverter postgreSqlSchemaConverter,
        PostgreSqlDeterministicOrderingResolver postgreSqlOrderingResolver,
        MongoMetadataDatabase mongoMetadataDatabase,
        MongoDbOptions mongoDbOptions,
        IMongoSourceSchemaInferenceService mongoSourceSchemaInferenceService,
        SourceSchemaComparisonService schemaComparisonService)
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
        var columns = await _postgreSqlMetadataDiscoveryService.DiscoverColumnsAsync(
                source.ConnectionProfile,
                source.Database,
                source.Schema,
                source.Table,
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
            _postgreSqlConnectionFactory,
            _postgreSqlMetadataDiscoveryService,
            _postgreSqlOrderingResolver,
            source);
    }

    private async Task<MongoDbEtlSource> CreateMongoDbSourceAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        var source = pipeline.MongoDbSource
            ?? throw new InvalidOperationException("The MongoDB source configuration is missing.");
        IReadOnlyList<SourceFieldDefinition> liveSchema;
        try
        {
            liveSchema = await _mongoSourceSchemaInferenceService
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
            _mongoMetadataDatabase,
            _mongoDbOptions,
            source,
            pipeline.ExpectedSchema);
    }
}
