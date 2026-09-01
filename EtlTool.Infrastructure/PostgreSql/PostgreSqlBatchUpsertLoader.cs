using System.Data.Common;
using System.Text;
using EtlTool.Application.Extraction;
using EtlTool.Application.Loading;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Connections;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using Npgsql;
using EtlTool.Infrastructure.Connections;

namespace EtlTool.Infrastructure.PostgreSql;

public sealed class PostgreSqlBatchUpsertLoader : IDataLoader
{
    private const string FailureMessage =
        "PostgreSQL batch loading failed without a confirmed write result.";

    private readonly IPostgreSqlConnectionFactory _connectionFactory;
    private readonly IPostgreSqlMetadataDiscoveryService _metadataDiscoveryService;
    private readonly IPostgreSqlDestinationAccessService _destinationAccessService;
    private readonly IPostgreSqlBatchExecutor _batchExecutor;
    private readonly int _maximumAttempts;
    private readonly TimeSpan _retryDelay;
    private readonly SavedConnectionProviderFactory? _savedConnectionProviderFactory;

    public PostgreSqlBatchUpsertLoader(
        IPostgreSqlConnectionFactory connectionFactory,
        IPostgreSqlMetadataDiscoveryService metadataDiscoveryService,
        IPostgreSqlDestinationAccessService destinationAccessService,
        PostgreSqlConnectionOptions options,
        SavedConnectionProviderFactory? savedConnectionProviderFactory = null)
        : this(
            connectionFactory,
            metadataDiscoveryService,
            destinationAccessService,
            options,
            new PostgreSqlBatchExecutor(),
            savedConnectionProviderFactory)
    {
    }

    internal PostgreSqlBatchUpsertLoader(
        IPostgreSqlConnectionFactory connectionFactory,
        IPostgreSqlMetadataDiscoveryService metadataDiscoveryService,
        IPostgreSqlDestinationAccessService destinationAccessService,
        PostgreSqlConnectionOptions options,
        IPostgreSqlBatchExecutor batchExecutor,
        SavedConnectionProviderFactory? savedConnectionProviderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(metadataDiscoveryService);
        ArgumentNullException.ThrowIfNull(destinationAccessService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(batchExecutor);
        options.Validate();

        _connectionFactory = connectionFactory;
        _metadataDiscoveryService = metadataDiscoveryService;
        _destinationAccessService = destinationAccessService;
        _batchExecutor = batchExecutor;
        _maximumAttempts = options.BatchWriteMaximumAttempts;
        _retryDelay = TimeSpan.FromMilliseconds(options.BatchWriteRetryDelayMilliseconds);
        _savedConnectionProviderFactory = savedConnectionProviderFactory;
    }

    public DestinationType DestinationType => DestinationType.PostgreSql;

    public async Task PrepareAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        try
        {
            var destination = ValidatePipelineConfiguration(pipeline);
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = await ResolveRuntimeAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            await runtime.DestinationAccess.EnsureDestinationAccessibleAsync(
                runtime.ConnectionProfile,
                destination.Database,
                destination.Schema,
                destination.Table,
                cancellationToken).ConfigureAwait(false);

            var columns = await runtime.MetadataDiscovery.DiscoverColumnsAsync(
                runtime.ConnectionProfile,
                destination.Database,
                destination.Schema,
                destination.Table,
                cancellationToken).ConfigureAwait(false);
            var constraints = await runtime.MetadataDiscovery.DiscoverKeyConstraintsAsync(
                runtime.ConnectionProfile,
                destination.Database,
                destination.Schema,
                destination.Table,
                cancellationToken).ConfigureAwait(false);

            PostgreSqlDestinationConfigurationValidator.Validate(
                destination,
                ActiveOutputFields(pipeline),
                columns,
                constraints);
            ValidateUpsertFieldMatchesDestinationMapping(pipeline, destination);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgreSqlDestinationPreparationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException
            or PostgreSqlConnectionProfileNotFoundException
            or PostgreSqlConnectionAccessException
            or PostgreSqlMetadataObjectNotFoundException)
        {
            throw new PostgreSqlDestinationPreparationException(exception);
        }
    }

    public async Task<BatchLoadResult> UpsertBatchAsync(
        IReadOnlyList<DataRow> rows,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("A PostgreSQL load batch cannot be empty.", nameof(rows));
        }

        var destination = ValidatePipelineConfiguration(pipeline);
        ValidateUpsertFieldMatchesDestinationMapping(pipeline, destination);
        cancellationToken.ThrowIfCancellationRequested();
        var command = CreateCommand(rows, pipeline, destination);
        var runtime = await ResolveRuntimeAsync(destination, cancellationToken)
            .ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _batchExecutor.ExecuteAsync(
                    runtime.ConnectionFactory,
                    runtime.ConnectionProfile,
                    destination.Database,
                    command,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PostgreSqlBatchAttemptException exception)
                when (exception.IsTransient
                    && exception.IsDefinitelyUncommitted
                    && attempt < _maximumAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgreSqlBatchAttemptException exception)
            {
                throw new BatchLoadException(
                    FailureMessage,
                    BatchLoadResult.Empty,
                    exception.InnerException ?? exception);
            }
        }
    }

    private async Task<PostgreSqlLoaderRuntime> ResolveRuntimeAsync(
        PostgreSqlDestinationOptions destination,
        CancellationToken cancellationToken)
    {
        if (!destination.SavedConnectionId.HasValue)
        {
            return new PostgreSqlLoaderRuntime(
                _connectionFactory,
                _metadataDiscoveryService,
                _destinationAccessService,
                destination.ConnectionProfile);
        }

        var providerFactory = _savedConnectionProviderFactory
            ?? throw new SavedConnectionResolutionException();
        var context = await providerFactory.CreatePostgreSqlAsync(
                destination.SavedConnectionId.Value,
                destination.SavedConnectionRevision
                    ?? throw new SavedConnectionResolutionException(),
                cancellationToken)
            .ConfigureAwait(false);
        return new PostgreSqlLoaderRuntime(
            context.ConnectionFactory,
            context.MetadataDiscovery,
            context.MetadataDiscovery,
            SavedConnectionProviderFactory.RuntimePostgreSqlProfile);
    }

    private sealed record PostgreSqlLoaderRuntime(
        IPostgreSqlConnectionFactory ConnectionFactory,
        IPostgreSqlMetadataDiscoveryService MetadataDiscovery,
        IPostgreSqlDestinationAccessService DestinationAccess,
        string ConnectionProfile);

    private static PostgreSqlDestinationOptions ValidatePipelineConfiguration(
        PipelineDefinition pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.DestinationType != DestinationType.PostgreSql)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL data loader cannot handle destination type '{pipeline.DestinationType}'.");
        }

        var destination = pipeline.PostgreSqlDestination;
        PostgreSqlDestinationConfigurationValidator.Validate(
            destination,
            ActiveOutputFields(pipeline));
        return destination!;
    }

    private static HashSet<string> ActiveOutputFields(PipelineDefinition pipeline) =>
        (pipeline.FieldMappings ?? [])
            .Where(mapping => mapping is not null
                && mapping.IsIncluded
                && !string.IsNullOrWhiteSpace(mapping.TargetField))
            .Select(mapping => mapping.TargetField)
            .ToHashSet(StringComparer.Ordinal);

    private static void ValidateUpsertFieldMatchesDestinationMapping(
        PipelineDefinition pipeline,
        PostgreSqlDestinationOptions destination)
    {
        var upsertMapping = destination.ColumnMappings.Single(mapping =>
            string.Equals(
                mapping.DestinationColumn,
                destination.UpsertKeyColumn,
                StringComparison.Ordinal));
        if (!string.Equals(pipeline.UpsertKeyField, upsertMapping.OutputField, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The PostgreSQL upsert key must use the output field mapped to its selected destination column.");
        }
    }

    private static PostgreSqlBatchCommand CreateCommand(
        IReadOnlyList<DataRow> rows,
        PipelineDefinition pipeline,
        PostgreSqlDestinationOptions destination)
    {
        var mappings = destination.ColumnMappings.ToArray();
        var values = new object?[rows.Count * mappings.Length];
        var sql = new StringBuilder();
        using var commandBuilder = new NpgsqlCommandBuilder();
        string Quote(string identifier) => commandBuilder.QuoteIdentifier(identifier);

        sql.Append("INSERT INTO ")
            .Append(Quote(destination.Schema))
            .Append('.')
            .Append(Quote(destination.Table))
            .Append(" (")
            .AppendJoin(", ", mappings.Select(mapping => Quote(mapping.DestinationColumn)))
            .Append(") VALUES ");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex]
                ?? throw new ArgumentException(
                    "The PostgreSQL load batch contains an invalid row.",
                    nameof(rows));
            _ = UpsertKeyIdentity.Create(row, pipeline.UpsertKeyField);

            if (rowIndex > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(');
            for (var mappingIndex = 0; mappingIndex < mappings.Length; mappingIndex++)
            {
                var mapping = mappings[mappingIndex];
                if (!row.Values.TryGetValue(mapping.OutputField, out var value))
                {
                    throw new InvalidOperationException(
                        $"PostgreSQL mapped output field '{mapping.OutputField}' is missing from row {row.SourceRowNumber}.");
                }

                var parameterIndex = rowIndex * mappings.Length + mappingIndex;
                values[parameterIndex] = value;
                if (mappingIndex > 0)
                {
                    sql.Append(", ");
                }

                sql.Append("@p").Append(parameterIndex);
            }

            sql.Append(')');
        }

        var nonKeyMappings = mappings
            .Where(mapping => !string.Equals(
                mapping.DestinationColumn,
                destination.UpsertKeyColumn,
                StringComparison.Ordinal))
            .ToArray();
        if (nonKeyMappings.Length == 0)
        {
            sql.Append(" ON CONFLICT (")
                .Append(Quote(destination.UpsertKeyColumn))
                .Append(") DO UPDATE SET ")
                .Append(Quote(destination.UpsertKeyColumn))
                .Append(" = EXCLUDED.")
                .Append(Quote(destination.UpsertKeyColumn));
        }
        else
        {
            sql.Append(" ON CONFLICT (")
                .Append(Quote(destination.UpsertKeyColumn))
                .Append(") DO UPDATE SET ")
                .AppendJoin(", ", nonKeyMappings.Select(mapping =>
                    $"{Quote(mapping.DestinationColumn)} = EXCLUDED.{Quote(mapping.DestinationColumn)}"));
        }

        sql.Append(" RETURNING (xmax = 0);");
        return new PostgreSqlBatchCommand(sql.ToString(), values, rows.Count);
    }
}

internal sealed record PostgreSqlBatchCommand(
    string CommandText,
    IReadOnlyList<object?> ParameterValues,
    int RowCount);

internal interface IPostgreSqlBatchExecutor
{
    Task<BatchLoadResult> ExecuteAsync(
        IPostgreSqlConnectionFactory connectionFactory,
        string connectionProfile,
        string database,
        PostgreSqlBatchCommand command,
        CancellationToken cancellationToken);
}

internal sealed class PostgreSqlBatchExecutor : IPostgreSqlBatchExecutor
{
    public async Task<BatchLoadResult> ExecuteAsync(
        IPostgreSqlConnectionFactory connectionFactory,
        string connectionProfile,
        string database,
        PostgreSqlBatchCommand command,
        CancellationToken cancellationToken)
    {
        DbTransaction? transaction = null;
        var commitStarted = false;
        BatchLoadResult? confirmedResult = null;
        try
        {
            await using var connection = await connectionFactory
                .OpenDatabaseAsync(connectionProfile, database, cancellationToken)
                .ConfigureAwait(false);
            transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using var dbCommand = connection.CreateCommand();
                dbCommand.Transaction = transaction;
                dbCommand.CommandText = command.CommandText;
                for (var index = 0; index < command.ParameterValues.Count; index++)
                {
                    var parameter = dbCommand.CreateParameter();
                    parameter.ParameterName = $"p{index}";
                    parameter.Value = command.ParameterValues[index] ?? DBNull.Value;
                    dbCommand.Parameters.Add(parameter);
                }

                long inserted = 0;
                long updated = 0;
                await using (var reader = await dbCommand
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.GetBoolean(0))
                        {
                            inserted++;
                        }
                        else
                        {
                            updated++;
                        }
                    }
                }

                if (inserted + updated != command.RowCount)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL did not confirm exactly one upsert result for every supplied row.");
                }

                commitStarted = true;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                confirmedResult = new BatchLoadResult(inserted, updated);
            }

            return confirmedResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (confirmedResult is not null)
        {
            throw new BatchLoadException(
                "PostgreSQL batch committed, but resource cleanup failed.",
                confirmedResult,
                exception);
        }
        catch (PostgreSqlConnectionAccessException exception)
        {
            throw new PostgreSqlBatchAttemptException(
                exception,
                exception.IsTransient,
                isDefinitelyUncommitted: true);
        }
        catch (NpgsqlException exception)
        {
            throw new PostgreSqlBatchAttemptException(
                exception,
                exception.IsTransient,
                isDefinitelyUncommitted: !commitStarted);
        }
        catch (TimeoutException exception)
        {
            throw new PostgreSqlBatchAttemptException(
                exception,
                isTransient: true,
                isDefinitelyUncommitted: !commitStarted);
        }
        catch (InvalidOperationException exception)
        {
            throw new PostgreSqlBatchAttemptException(
                exception,
                isTransient: false,
                isDefinitelyUncommitted: !commitStarted);
        }
    }
}

internal sealed class PostgreSqlBatchAttemptException : Exception
{
    public PostgreSqlBatchAttemptException(
        Exception innerException,
        bool isTransient,
        bool isDefinitelyUncommitted)
        : base("PostgreSQL batch attempt failed.", innerException)
    {
        IsTransient = isTransient;
        IsDefinitelyUncommitted = isDefinitelyUncommitted;
    }

    public bool IsTransient { get; }

    public bool IsDefinitelyUncommitted { get; }
}
