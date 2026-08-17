using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;

namespace EtlTool.UnitTests.Application.Pipelines;

public sealed class PipelineServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 8, 17, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_AssignsLifecyclePreservesMetadataAndPassesCancellation()
    {
        var repository = new RecordingPipelineDefinitionRepository();
        var timeProvider = new FixedTimeProvider(FixedUtcNow);
        var service = new PipelineService(repository, timeProvider);
        var originalId = Guid.NewGuid();
        var originalCreatedAt = FixedUtcNow.AddDays(-2);
        var originalUpdatedAt = FixedUtcNow.AddDays(-1);
        var transformations = new List<TransformationRule>
        {
            new() { Id = Guid.NewGuid(), Order = 1 }
        };
        var pipeline = new PipelineDefinition
        {
            Id = originalId,
            Name = "  Draft pipeline  ",
            Description = "Preserve me",
            DestinationDatabase = string.Empty,
            DestinationCollection = string.Empty,
            UpsertKeyField = string.Empty,
            CreatedAt = originalCreatedAt,
            UpdatedAt = originalUpdatedAt,
            TransformationRules = transformations
        };
        using var cancellationSource = new CancellationTokenSource();

        var result = await service.CreateAsync(pipeline, cancellationSource.Token);

        Assert.Same(pipeline, result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.NotEqual(originalId, result.Id);
        Assert.Equal(FixedUtcNow, result.CreatedAt);
        Assert.Equal(FixedUtcNow, result.UpdatedAt);
        Assert.Equal("  Draft pipeline  ", result.Name);
        Assert.Equal("Preserve me", result.Description);
        Assert.Same(transformations, result.TransformationRules);
        Assert.Equal(string.Empty, result.DestinationDatabase);
        Assert.Same(result, repository.AddedPipeline);
        Assert.Equal(result.Id, repository.AddedPipelineId);
        Assert.Equal(FixedUtcNow, repository.AddedPipelineCreatedAt);
        Assert.Equal(FixedUtcNow, repository.AddedPipelineUpdatedAt);
        Assert.Equal(cancellationSource.Token, repository.AddCancellationToken);
        Assert.Equal(1, repository.AddCallCount);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsNullAndBlankNamesBeforeRepositoryAccess()
    {
        var repository = new RecordingPipelineDefinitionRepository();
        var service = CreateService(repository);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.CreateAsync(null!, CancellationToken.None));

        foreach (var name in new[] { string.Empty, " ", "\t\r\n" })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => service.CreateAsync(
                    new PipelineDefinition { Name = name },
                    CancellationToken.None));
        }

        Assert.Equal(0, repository.AddCallCount);
    }

    [Fact]
    public async Task CreateAsync_PreservesDuplicatePipelineException()
    {
        var duplicateException = new DuplicatePipelineDefinitionException(Guid.NewGuid());
        var repository = new RecordingPipelineDefinitionRepository
        {
            AddHandler = (_, _) => Task.FromException(duplicateException)
        };
        var service = CreateService(repository);

        var actual = await Assert.ThrowsAsync<DuplicatePipelineDefinitionException>(
            () => service.CreateAsync(
                new PipelineDefinition { Name = "Duplicate" },
                CancellationToken.None));

        Assert.Same(duplicateException, actual);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsExistingAndMissingResultsAndPassesCancellation()
    {
        var existing = new PipelineDefinition { Id = Guid.NewGuid(), Name = "Existing" };
        var repository = new RecordingPipelineDefinitionRepository
        {
            GetByIdHandler = (id, _) => Task.FromResult(
                id == existing.Id ? existing : null)
        };
        var service = CreateService(repository);
        var missingId = Guid.NewGuid();
        using var cancellationSource = new CancellationTokenSource();

        var found = await service.GetByIdAsync(existing.Id, cancellationSource.Token);
        var missing = await service.GetByIdAsync(missingId, cancellationSource.Token);

        Assert.Same(existing, found);
        Assert.Null(missing);
        Assert.Equal([existing.Id, missingId], repository.GetByIdIds);
        Assert.All(
            repository.GetByIdCancellationTokens,
            token => Assert.Equal(cancellationSource.Token, token));
    }

    [Fact]
    public async Task GetByIdAsync_RejectsEmptyIdBeforeRepositoryAccess()
    {
        var repository = new RecordingPipelineDefinitionRepository();
        var service = CreateService(repository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetByIdAsync(Guid.Empty, CancellationToken.None));

        Assert.Equal(0, repository.GetByIdCallCount);
    }

    [Fact]
    public async Task ListAsync_ReturnsRepositoryResultAndPassesCancellation()
    {
        IReadOnlyList<PipelineDefinition> expected =
        [
            new PipelineDefinition { Id = Guid.NewGuid(), Name = "First" },
            new PipelineDefinition { Id = Guid.NewGuid(), Name = "Second" }
        ];
        var repository = new RecordingPipelineDefinitionRepository
        {
            ListHandler = _ => Task.FromResult(expected)
        };
        var service = CreateService(repository);
        using var cancellationSource = new CancellationTokenSource();

        var result = await service.ListAsync(cancellationSource.Token);

        Assert.Same(expected, result);
        Assert.Equal(cancellationSource.Token, repository.ListCancellationToken);
        Assert.Equal(1, repository.ListCallCount);
    }

    [Fact]
    public async Task UpdateAsync_ExistingPipelinePreservesCreationAndRefreshesUpdateTime()
    {
        var id = Guid.NewGuid();
        var persistedCreatedAt = FixedUtcNow.AddMonths(-1);
        var existing = new PipelineDefinition
        {
            Id = id,
            Name = "Existing",
            CreatedAt = persistedCreatedAt,
            UpdatedAt = FixedUtcNow.AddDays(-1)
        };
        var replacement = new PipelineDefinition
        {
            Id = Guid.Empty,
            Name = "Replacement",
            CreatedAt = FixedUtcNow.AddYears(-1),
            UpdatedAt = FixedUtcNow.AddYears(-1)
        };
        var repository = new RecordingPipelineDefinitionRepository
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(existing),
            UpdateHandler = (_, _) => Task.FromResult(true)
        };
        var timeProvider = new FixedTimeProvider(FixedUtcNow);
        var service = new PipelineService(repository, timeProvider);
        using var cancellationSource = new CancellationTokenSource();

        var result = await service.UpdateAsync(id, replacement, cancellationSource.Token);

        Assert.True(result);
        Assert.Equal(id, replacement.Id);
        Assert.Equal(persistedCreatedAt, replacement.CreatedAt);
        Assert.Equal(FixedUtcNow, replacement.UpdatedAt);
        Assert.Same(replacement, repository.UpdatedPipeline);
        Assert.Equal(id, repository.UpdatedPipelineId);
        Assert.Equal(persistedCreatedAt, repository.UpdatedPipelineCreatedAt);
        Assert.Equal(FixedUtcNow, repository.UpdatedPipelineUpdatedAt);
        Assert.Equal(1, repository.GetByIdCallCount);
        Assert.Equal(1, repository.UpdateCallCount);
        Assert.Equal(cancellationSource.Token, repository.GetByIdCancellationTokens.Single());
        Assert.Equal(cancellationSource.Token, repository.UpdateCancellationToken);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidInputBeforeRepositoryAccess()
    {
        var id = Guid.NewGuid();
        var repository = new RecordingPipelineDefinitionRepository();
        var service = CreateService(repository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync(
                Guid.Empty,
                new PipelineDefinition { Name = "Valid" },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.UpdateAsync(id, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync(
                id,
                new PipelineDefinition { Name = " " },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync(
                id,
                new PipelineDefinition { Id = Guid.NewGuid(), Name = "Mismatch" },
                CancellationToken.None));

        Assert.Equal(0, repository.GetByIdCallCount);
        Assert.Equal(0, repository.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateAsync_MissingPipelineReturnsFalseWithoutMutationOrUpdateCall()
    {
        var originalCreatedAt = FixedUtcNow.AddDays(-3);
        var originalUpdatedAt = FixedUtcNow.AddDays(-2);
        var replacement = new PipelineDefinition
        {
            Name = "Missing",
            CreatedAt = originalCreatedAt,
            UpdatedAt = originalUpdatedAt
        };
        var repository = new RecordingPipelineDefinitionRepository();
        var service = CreateService(repository);

        var result = await service.UpdateAsync(
            Guid.NewGuid(),
            replacement,
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(Guid.Empty, replacement.Id);
        Assert.Equal(originalCreatedAt, replacement.CreatedAt);
        Assert.Equal(originalUpdatedAt, replacement.UpdatedAt);
        Assert.Equal(1, repository.GetByIdCallCount);
        Assert.Equal(0, repository.UpdateCallCount);
    }

    [Fact]
    public async Task UpdateAsync_PreservesFalseResultFromRepositoryRace()
    {
        var id = Guid.NewGuid();
        var repository = new RecordingPipelineDefinitionRepository
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(
                new PipelineDefinition
                {
                    Id = id,
                    Name = "Existing",
                    CreatedAt = FixedUtcNow.AddDays(-1)
                }),
            UpdateHandler = (_, _) => Task.FromResult(false)
        };
        var service = CreateService(repository);

        var result = await service.UpdateAsync(
            id,
            new PipelineDefinition { Id = id, Name = "Replacement" },
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, repository.UpdateCallCount);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsRepositoryResultsAndRejectsEmptyId()
    {
        var existingId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var repository = new RecordingPipelineDefinitionRepository
        {
            DeleteHandler = (id, _) => Task.FromResult(id == existingId)
        };
        var service = CreateService(repository);
        using var cancellationSource = new CancellationTokenSource();

        Assert.True(await service.DeleteAsync(existingId, cancellationSource.Token));
        Assert.False(await service.DeleteAsync(missingId, cancellationSource.Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DeleteAsync(Guid.Empty, cancellationSource.Token));

        Assert.Equal([existingId, missingId], repository.DeleteIds);
        Assert.All(
            repository.DeleteCancellationTokens,
            token => Assert.Equal(cancellationSource.Token, token));
    }

    [Fact]
    public async Task ListAsync_AlreadyCancelledOperationRemainsCancellation()
    {
        var repository = new RecordingPipelineDefinitionRepository
        {
            ListHandler = cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<PipelineDefinition>>([]);
            }
        };
        var service = CreateService(repository);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ListAsync(cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, repository.ListCancellationToken);
    }

    [Fact]
    public async Task ListAsync_PreservesRepositoryFailure()
    {
        var expected = new InvalidOperationException("Persistence failed.");
        var repository = new RecordingPipelineDefinitionRepository
        {
            ListHandler = _ => Task.FromException<IReadOnlyList<PipelineDefinition>>(expected)
        };
        var service = CreateService(repository);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListAsync(CancellationToken.None));

        Assert.Same(expected, actual);
    }

    private static PipelineService CreateService(
        RecordingPipelineDefinitionRepository repository)
    {
        return new PipelineService(repository, new FixedTimeProvider(FixedUtcNow));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;
            return utcNow;
        }
    }

    private sealed class RecordingPipelineDefinitionRepository : IPipelineDefinitionRepository
    {
        public Func<PipelineDefinition, CancellationToken, Task>? AddHandler { get; init; }

        public Func<Guid, CancellationToken, Task<PipelineDefinition?>>? GetByIdHandler { get; init; }

        public Func<CancellationToken, Task<IReadOnlyList<PipelineDefinition>>>? ListHandler { get; init; }

        public Func<PipelineDefinition, CancellationToken, Task<bool>>? UpdateHandler { get; init; }

        public Func<Guid, CancellationToken, Task<bool>>? DeleteHandler { get; init; }

        public int AddCallCount { get; private set; }

        public PipelineDefinition? AddedPipeline { get; private set; }

        public Guid AddedPipelineId { get; private set; }

        public DateTimeOffset AddedPipelineCreatedAt { get; private set; }

        public DateTimeOffset AddedPipelineUpdatedAt { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public int GetByIdCallCount => GetByIdIds.Count;

        public List<Guid> GetByIdIds { get; } = [];

        public List<CancellationToken> GetByIdCancellationTokens { get; } = [];

        public int ListCallCount { get; private set; }

        public CancellationToken ListCancellationToken { get; private set; }

        public int UpdateCallCount { get; private set; }

        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public Guid UpdatedPipelineId { get; private set; }

        public DateTimeOffset UpdatedPipelineCreatedAt { get; private set; }

        public DateTimeOffset UpdatedPipelineUpdatedAt { get; private set; }

        public CancellationToken UpdateCancellationToken { get; private set; }

        public List<Guid> DeleteIds { get; } = [];

        public List<CancellationToken> DeleteCancellationTokens { get; } = [];

        public Task AddAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            AddCallCount++;
            AddedPipeline = pipeline;
            AddedPipelineId = pipeline.Id;
            AddedPipelineCreatedAt = pipeline.CreatedAt;
            AddedPipelineUpdatedAt = pipeline.UpdatedAt;
            AddCancellationToken = cancellationToken;

            return AddHandler?.Invoke(pipeline, cancellationToken) ?? Task.CompletedTask;
        }

        public Task<PipelineDefinition?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            GetByIdIds.Add(id);
            GetByIdCancellationTokens.Add(cancellationToken);

            return GetByIdHandler?.Invoke(id, cancellationToken)
                ?? Task.FromResult<PipelineDefinition?>(null);
        }

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(
            CancellationToken cancellationToken)
        {
            ListCallCount++;
            ListCancellationToken = cancellationToken;

            return ListHandler?.Invoke(cancellationToken)
                ?? Task.FromResult<IReadOnlyList<PipelineDefinition>>([]);
        }

        public Task<bool> UpdateAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            UpdatedPipeline = pipeline;
            UpdatedPipelineId = pipeline.Id;
            UpdatedPipelineCreatedAt = pipeline.CreatedAt;
            UpdatedPipelineUpdatedAt = pipeline.UpdatedAt;
            UpdateCancellationToken = cancellationToken;

            return UpdateHandler?.Invoke(pipeline, cancellationToken)
                ?? Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            DeleteIds.Add(id);
            DeleteCancellationTokens.Add(cancellationToken);

            return DeleteHandler?.Invoke(id, cancellationToken)
                ?? Task.FromResult(false);
        }
    }
}
