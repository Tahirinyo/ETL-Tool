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
    private readonly PostgreSqlDeterministicOrderingResolver _postgreSqlOrderingResolver;
    private readonly MongoMetadataDatabase _mongoMetadataDatabase;
    private readonly MongoDbOptions _mongoDbOptions;
    private readonly IMongoSourceSchemaInferenceService _mongoSourceSchemaInferenceService;
    private readonly SourceSchemaComparisonService _schemaComparisonService;

    public PreviewSourceFactory(
        IWizardSourceStore wizardSourceStore,
        IPostgreSqlConnectionFactory postgreSqlConnectionFactory,
        IPostgreSqlMetadataDiscoveryService postgreSqlMetadataDiscoveryService,
        PostgreSqlDeterministicOrderingResolver postgreSqlOrderingResolver,
        MongoMetadataDatabase mongoMetadataDatabase,
        MongoDbOptions mongoDbOptions,
        IMongoSourceSchemaInferenceService mongoSourceSchemaInferenceService,
        SourceSchemaComparisonService schemaComparisonService)
    {
        ArgumentNullException.ThrowIfNull(wizardSourceStore);
        ArgumentNullException.ThrowIfNull(postgreSqlConnectionFactory);
        ArgumentNullException.ThrowIfNull(postgreSqlMetadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(postgreSqlOrderingResolver);
        ArgumentNullException.ThrowIfNull(mongoMetadataDatabase);
        ArgumentNullException.ThrowIfNull(mongoDbOptions);
        ArgumentNullException.ThrowIfNull(mongoSourceSchemaInferenceService);
        ArgumentNullException.ThrowIfNull(schemaComparisonService);

        _wizardSourceStore = wizardSourceStore;
        _postgreSqlConnectionFactory = postgreSqlConnectionFactory;
        _postgreSqlMetadataDiscoveryService = postgreSqlMetadataDiscoveryService;
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
            SourceType.PostgreSql => CreatePostgreSqlSource(pipeline),
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

    private PostgreSqlEtlSource CreatePostgreSqlSource(PipelineDefinition pipeline) => new(
        _postgreSqlConnectionFactory,
        _postgreSqlMetadataDiscoveryService,
        _postgreSqlOrderingResolver,
        pipeline.PostgreSqlSource
            ?? throw new InvalidOperationException("The PostgreSQL source configuration is missing."));

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
