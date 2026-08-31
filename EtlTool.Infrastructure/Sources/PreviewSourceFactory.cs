using EtlTool.Application.Extraction;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Infrastructure.PostgreSql;

namespace EtlTool.Infrastructure.Sources;

public sealed class PreviewSourceFactory : IPreviewSourceFactory
{
    private readonly IWizardSourceStore _wizardSourceStore;
    private readonly IPostgreSqlConnectionFactory _postgreSqlConnectionFactory;
    private readonly IPostgreSqlMetadataDiscoveryService _postgreSqlMetadataDiscoveryService;
    private readonly PostgreSqlDeterministicOrderingResolver _postgreSqlOrderingResolver;

    public PreviewSourceFactory(
        IWizardSourceStore wizardSourceStore,
        IPostgreSqlConnectionFactory postgreSqlConnectionFactory,
        IPostgreSqlMetadataDiscoveryService postgreSqlMetadataDiscoveryService,
        PostgreSqlDeterministicOrderingResolver postgreSqlOrderingResolver)
    {
        ArgumentNullException.ThrowIfNull(wizardSourceStore);
        ArgumentNullException.ThrowIfNull(postgreSqlConnectionFactory);
        ArgumentNullException.ThrowIfNull(postgreSqlMetadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(postgreSqlOrderingResolver);

        _wizardSourceStore = wizardSourceStore;
        _postgreSqlConnectionFactory = postgreSqlConnectionFactory;
        _postgreSqlMetadataDiscoveryService = postgreSqlMetadataDiscoveryService;
        _postgreSqlOrderingResolver = postgreSqlOrderingResolver;
    }

    public Task<IEtlSource?> AcquireAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        cancellationToken.ThrowIfCancellationRequested();

        return pipeline.SourceType switch
        {
            SourceType.Csv or SourceType.Xlsx => AcquireFileSourceAsync(pipeline, cancellationToken),
            SourceType.PostgreSql => Task.FromResult<IEtlSource?>(CreatePostgreSqlSource(pipeline)),
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
}
