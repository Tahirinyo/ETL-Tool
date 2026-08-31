using System.Data.Common;
using System.Runtime.CompilerServices;
using EtlTool.Application.Extraction;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.Sources;

namespace EtlTool.UnitTests.Infrastructure.Sources;

public sealed class PreviewSourceFactoryTests
{
    [Theory]
    [InlineData(SourceType.Csv)]
    [InlineData(SourceType.Xlsx)]
    public async Task AcquireAsync_FileSourceDelegatesToWizardSourceStore(SourceType sourceType)
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);
        var pipeline = Pipeline(sourceType);

        var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.Same(store.Lease, source);
        Assert.Equal(1, store.AcquireCallCount);
        Assert.Equal(pipeline.Id, store.PipelineId);
        Assert.Equal(sourceType, store.SourceType);
        Assert.Same(pipeline.SourceOptions, store.SourceOptions);
    }

    [Fact]
    public async Task AcquireAsync_PostgreSqlCreatesTheExistingPostgreSqlEtlSourceWithoutFileAcquisition()
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);
        var pipeline = Pipeline(SourceType.PostgreSql);
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };

        await using var source = await factory.AcquireAsync(pipeline, CancellationToken.None);

        Assert.IsType<PostgreSqlEtlSource>(source);
        Assert.Equal(0, store.AcquireCallCount);
    }

    [Fact]
    public async Task AcquireAsync_RejectsUnsupportedOrIncompleteSourcesAndPreservesCancellation()
    {
        var store = new RecordingWizardSourceStore();
        var factory = CreateFactory(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.Unspecified), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.PostgreSql), CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.AcquireAsync(Pipeline(SourceType.Csv), cancellation.Token));
        Assert.Equal(0, store.AcquireCallCount);
    }

    private static PreviewSourceFactory CreateFactory(RecordingWizardSourceStore store) => new(
        store,
        new ThrowingConnectionFactory(),
        new ThrowingMetadataDiscoveryService(),
        new PostgreSqlDeterministicOrderingResolver());

    private static PipelineDefinition Pipeline(SourceType sourceType) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = sourceType,
        SourceOptions = new SourceOptions { FirstRowIsHeader = true }
    };

    private sealed class RecordingWizardSourceStore : IWizardSourceStore
    {
        public IWizardSourceLease Lease { get; } = new EmptyLease();
        public int AcquireCallCount { get; private set; }
        public Guid PipelineId { get; private set; }
        public SourceType SourceType { get; private set; }
        public SourceOptions? SourceOptions { get; private set; }

        public Task<bool> ActivateAsync(Guid pipelineId, Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardAsync(Guid sourceReferenceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IWizardSourceLease?> AcquireAsync(
            Guid pipelineId,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            AcquireCallCount++;
            PipelineId = pipelineId;
            SourceType = sourceType;
            SourceOptions = sourceOptions;
            return Task.FromResult<IWizardSourceLease?>(Lease);
        }

        public Task RemoveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RetireActiveAsync(Guid pipelineId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyLease : IWizardSourceLease
    {
        public async IAsyncEnumerable<DataRow> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingConnectionFactory : IPostgreSqlConnectionFactory
    {
        public Task<DbConnection> OpenAsync(string connectionProfile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DbConnection> OpenDatabaseAsync(
            string connectionProfile,
            string database,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingMetadataDiscoveryService : IPostgreSqlMetadataDiscoveryService
    {
        public Task<IReadOnlyList<PostgreSqlDatabaseMetadata>> DiscoverDatabasesAsync(string connectionProfile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlSchemaMetadata>> DiscoverSchemasAsync(string connectionProfile, string database, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlTableMetadata>> DiscoverTablesAsync(string connectionProfile, string database, string schema, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlColumnMetadata>> DiscoverColumnsAsync(string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostgreSqlKeyConstraintMetadata>> DiscoverKeyConstraintsAsync(string connectionProfile, string database, string schema, string table, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
