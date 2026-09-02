using EtlTool.Application.Connections;
using EtlTool.Application.MongoDB;
using EtlTool.Application.PostgreSql;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.PostgreSql;
using System.Collections.Concurrent;

namespace EtlTool.Infrastructure.Connections;

public interface ISavedConnectionRuntimeContextFactory
{
    Task<PostgreSqlRuntimeConnectionContext> CreatePostgreSqlAsync(
        Guid connectionId,
        int revision,
        CancellationToken cancellationToken);

    Task<MongoRuntimeConnectionContext> CreateMongoDbAsync(
        Guid connectionId,
        int revision,
        CancellationToken cancellationToken);
}

public sealed class SavedConnectionProviderFactory : ISavedConnectionRuntimeContextFactory
{
    public const string RuntimePostgreSqlProfile = "__saved_connection_runtime";

    private readonly ISavedConnectionRuntimeResolver _resolver;
    private readonly MongoDbOptions _mongoDbOptions;
    private readonly PostgreSqlConnectionOptions _postgreSqlOptions;
    private readonly ConcurrentDictionary<(Guid Id, int Revision), Lazy<Task<PostgreSqlRuntimeConnectionContext>>>
        _postgreSqlContexts = [];
    private readonly ConcurrentDictionary<(Guid Id, int Revision), Lazy<Task<MongoRuntimeConnectionContext>>>
        _mongoContexts = [];

    public SavedConnectionProviderFactory(
        ISavedConnectionRuntimeResolver resolver,
        MongoDbOptions mongoDbOptions,
        PostgreSqlConnectionOptions postgreSqlOptions)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(mongoDbOptions);
        ArgumentNullException.ThrowIfNull(postgreSqlOptions);
        _resolver = resolver;
        _mongoDbOptions = mongoDbOptions;
        _postgreSqlOptions = postgreSqlOptions;
    }

    public Task<PostgreSqlRuntimeConnectionContext> CreatePostgreSqlAsync(
        Guid connectionId,
        int revision,
        CancellationToken cancellationToken)
    {
        _ = Reference(connectionId, revision, DatabaseProviderType.PostgreSql);
        var key = (connectionId, revision);
        var lazy = _postgreSqlContexts.GetOrAdd(
            key,
            _ => new Lazy<Task<PostgreSqlRuntimeConnectionContext>>(
                () => CreatePostgreSqlCoreAsync(connectionId, revision, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndRemoveFailedAsync(_postgreSqlContexts, key, lazy, cancellationToken);
    }

    private async Task<PostgreSqlRuntimeConnectionContext> CreatePostgreSqlCoreAsync(
        Guid connectionId,
        int revision,
        CancellationToken cancellationToken)
    {
        var configuration = await _resolver.ResolveConfigurationAsync(
            Reference(connectionId, revision, DatabaseProviderType.PostgreSql),
            cancellationToken).ConfigureAwait(false);
        var options = new PostgreSqlConnectionOptions
        {
            BatchWriteMaximumAttempts = _postgreSqlOptions.BatchWriteMaximumAttempts,
            BatchWriteRetryDelayMilliseconds = _postgreSqlOptions.BatchWriteRetryDelayMilliseconds,
            Profiles = new Dictionary<string, PostgreSqlConnectionProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                [RuntimePostgreSqlProfile] = new PostgreSqlConnectionProfileOptions
                {
                    ConnectionString = configuration
                }
            }
        };
        var factory = new PostgreSqlConnectionFactory(options);
        var discovery = new PostgreSqlMetadataDiscoveryService(factory);
        return new PostgreSqlRuntimeConnectionContext(factory, discovery);
    }

    public Task<MongoRuntimeConnectionContext> CreateMongoDbAsync(
        Guid connectionId,
        int revision,
        CancellationToken cancellationToken)
    {
        _ = Reference(connectionId, revision, DatabaseProviderType.MongoDb);
        var key = (connectionId, revision);
        var lazy = _mongoContexts.GetOrAdd(
            key,
            _ => new Lazy<Task<MongoRuntimeConnectionContext>>(
                () => CreateMongoDbCoreAsync(connectionId, revision, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndRemoveFailedAsync(_mongoContexts, key, lazy, cancellationToken);
    }

    private async Task<MongoRuntimeConnectionContext> CreateMongoDbCoreAsync(
        Guid connectionId,
        int revision,
        CancellationToken cancellationToken)
    {
        var configuration = await _resolver.ResolveConfigurationAsync(
            Reference(connectionId, revision, DatabaseProviderType.MongoDb),
            cancellationToken).ConfigureAwait(false);
        var options = new MongoDbOptions
        {
            ConnectionString = configuration,
            MetadataDatabaseName = _mongoDbOptions.MetadataDatabaseName,
            BulkWriteMaximumAttempts = _mongoDbOptions.BulkWriteMaximumAttempts,
            BulkWriteRetryDelayMilliseconds = _mongoDbOptions.BulkWriteRetryDelayMilliseconds,
            SourceSchemaSampleDocumentLimit = _mongoDbOptions.SourceSchemaSampleDocumentLimit,
            SourceExecutionFetchSize = _mongoDbOptions.SourceExecutionFetchSize
        };
        var metadata = new MongoMetadataDatabase(options);
        var targetAccess = new MongoTargetAccessService(metadata, options);
        var discovery = new MongoSourceMetadataDiscoveryService(metadata, targetAccess);
        var inference = new MongoSourceSchemaInferenceService(metadata, discovery, options);
        return new MongoRuntimeConnectionContext(metadata, targetAccess, inference, options);
    }

    private static async Task<TContext> AwaitAndRemoveFailedAsync<TContext>(
        ConcurrentDictionary<(Guid Id, int Revision), Lazy<Task<TContext>>> cache,
        (Guid Id, int Revision) key,
        Lazy<Task<TContext>> lazy,
        CancellationToken cancellationToken)
    {
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            cache.TryRemove(new KeyValuePair<(Guid Id, int Revision), Lazy<Task<TContext>>>(key, lazy));
            throw;
        }
    }

    private static SavedConnectionReference Reference(
        Guid connectionId,
        int revision,
        DatabaseProviderType providerType)
    {
        if (connectionId == Guid.Empty || revision < 1)
        {
            throw new SavedConnectionResolutionException();
        }

        return new SavedConnectionReference
        {
            ConnectionId = connectionId,
            ProviderType = providerType,
            Revision = revision
        };
    }
}

public sealed record PostgreSqlRuntimeConnectionContext(
    IPostgreSqlConnectionFactory ConnectionFactory,
    IPostgreSqlRuntimeMetadataDiscoveryService MetadataDiscovery);

public sealed record MongoRuntimeConnectionContext(
    MongoMetadataDatabase MetadataDatabase,
    MongoTargetAccessService TargetAccess,
    IMongoSourceSchemaInferenceService SchemaInference,
    MongoDbOptions Options);
