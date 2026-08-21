using EtlTool.Application.Pipelines;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Transformations;

public sealed class TransformationRuleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_DefaultValuePreservesEmptyValueAndAppendsNextOrder()
    {
        var pipeline = Pipeline(rules: [Rule(4, TransformationType.Trim)]);
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        var created = await service.CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(TransformationType.SetDefaultValue, "name", "", null, null),
            CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(5, created.Order);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("", created.Configuration["Value"]);
        Assert.Equal([4, 5], repository.UpdatedPipeline!.TransformationRules.Select(rule => rule.Order));
        Assert.Equal(Now, repository.UpdatedPipeline.UpdatedAt);
    }

    [Fact]
    public async Task CreateAsync_FindAndReplaceUsesExactKeysAndPreservesWhitespace()
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        var created = await service.CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(TransformationType.FindAndReplace, "name", null, " ", ""),
            CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(" ", created.Configuration["Find"]);
        Assert.Equal("", created.Configuration["Replace"]);
        Assert.Equal(["Find", "Replace"], created.Configuration.Keys.OrderBy(key => key));
    }

    [Theory]
    [InlineData(TransformationType.Trim)]
    [InlineData(TransformationType.ToUpper)]
    [InlineData(TransformationType.ToLower)]
    public async Task CreateAsync_FirstRuleStartsAtOneAndDropsIrrelevantConfiguration(TransformationType type)
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);

        var created = await CreateService(repository).CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(type, "name", "ignored", "ignored", "ignored"),
            CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(1, created.Order);
        Assert.Empty(created.Configuration);
    }

    [Theory]
    [InlineData(TransformationType.Unspecified)]
    [InlineData(TransformationType.ConvertToString)]
    [InlineData(TransformationType.ConvertToInteger)]
    [InlineData(TransformationType.ConvertToDecimal)]
    [InlineData(TransformationType.ConvertToDate)]
    [InlineData(TransformationType.FilterRow)]
    [InlineData(TransformationType.Deduplicate)]
    [InlineData((TransformationType)999)]
    public async Task CreateAsync_RejectsUnsupportedTransformationTypes(TransformationType type)
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateService(repository).CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(type, "name", null, null, null),
            CancellationToken.None));

        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task CreateAsync_RejectsUnavailableOrNonOrdinalMappedSourceFields()
    {
        var pipeline = Pipeline();
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Name", TargetField = "renamedName", IsIncluded = true },
            new FieldMapping { SourceField = "Hidden", TargetField = "hidden", IsIncluded = false }
        ];
        var repository = new RecordingRepository(pipeline);

        foreach (var field in new[] { "", " ", "Name", "renamedname", "hidden" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => CreateService(repository).CreateAsync(
                pipeline.Id,
                new TransformationRuleInput(TransformationType.Trim, field, null, null, null),
                CancellationToken.None));
        }

        pipeline.FieldMappings = [];
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService(repository).CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(TransformationType.Trim, "renamedName", null, null, null),
            CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue)]
    [InlineData(TransformationType.FindAndReplace)]
    public async Task CreateAsync_RejectsMissingRequiredConfiguration(TransformationType type)
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);
        var input = type == TransformationType.SetDefaultValue
            ? new TransformationRuleInput(type, "name", null, null, null)
            : new TransformationRuleInput(type, "name", null, "find", null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(pipeline.Id, input, CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task UpdateAsync_PreservesIdentityOrderAndUnrelatedAggregateState()
    {
        var existing = Rule(7, TransformationType.Trim);
        var pipeline = Pipeline(rules: [existing]);
        pipeline.Description = "Keep me";
        pipeline.ValidationRules = [new ValidationRule { Id = Guid.NewGuid(), Field = "name" }];
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        var updated = await service.UpdateAsync(
            pipeline.Id,
            existing.Id,
            new TransformationRuleInput(TransformationType.FindAndReplace, "name", null, "a", ""),
            CancellationToken.None);

        Assert.True(updated);
        var persisted = Assert.Single(repository.UpdatedPipeline!.TransformationRules);
        Assert.Equal(existing.Id, persisted.Id);
        Assert.Equal(7, persisted.Order);
        Assert.Equal("", persisted.Configuration["Replace"]);
        Assert.Equal("Keep me", repository.UpdatedPipeline.Description);
        Assert.Same(pipeline.ValidationRules, repository.UpdatedPipeline.ValidationRules);
    }

    [Fact]
    public async Task UpdateAsync_TypeChangeRemovesObsoleteConfigurationAndRejectsWrongRuleOwnership()
    {
        var existing = Rule(7, TransformationType.FindAndReplace);
        existing.Configuration["Find"] = "a";
        existing.Configuration["Replace"] = "b";
        var pipeline = Pipeline(rules: [existing]);
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        Assert.True(await service.UpdateAsync(
            pipeline.Id,
            existing.Id,
            new TransformationRuleInput(TransformationType.Trim, "name", "ignored", "ignored", "ignored"),
            CancellationToken.None));
        Assert.Empty(repository.UpdatedPipeline!.TransformationRules.Single().Configuration);
        Assert.Equal(existing.Id, repository.UpdatedPipeline.TransformationRules.Single().Id);
        Assert.Equal(7, repository.UpdatedPipeline.TransformationRules.Single().Order);

        repository.ResetUpdate();
        Assert.False(await service.UpdateAsync(
            pipeline.Id,
            Guid.NewGuid(),
            new TransformationRuleInput(TransformationType.Trim, "name", null, null, null),
            CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task UpdateAndDeleteAsync_CannotMutateRuleOwnedByAnotherPipeline()
    {
        var pipelineA = Pipeline();
        var ruleB = Rule(1, TransformationType.Trim);
        var pipelineB = Pipeline(rules: [ruleB]);
        var repository = new RecordingRepository(pipelineA, pipelineB);
        var service = CreateService(repository);

        Assert.False(await service.UpdateAsync(
            pipelineA.Id,
            ruleB.Id,
            new TransformationRuleInput(TransformationType.ToUpper, "name", null, null, null),
            CancellationToken.None));
        Assert.False(await service.DeleteAsync(pipelineA.Id, ruleB.Id, CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
        Assert.Equal(ruleB.Id, pipelineB.TransformationRules.Single().Id);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTargetAndPreservesGappedOrders()
    {
        var first = Rule(2, TransformationType.Trim);
        var target = Rule(8, TransformationType.ToLower);
        var last = Rule(20, TransformationType.ToUpper);
        var pipeline = Pipeline(rules: [first, target, last]);
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        Assert.True(await service.DeleteAsync(pipeline.Id, target.Id, CancellationToken.None));
        Assert.Equal([first.Id, last.Id], repository.UpdatedPipeline!.TransformationRules.Select(rule => rule.Id));
        Assert.Equal([2, 20], repository.UpdatedPipeline.TransformationRules.Select(rule => rule.Order));
    }

    [Fact]
    public async Task ReorderAsync_NormalizesOrdersAndPreservesRuleAndAggregateState()
    {
        var first = Rule(20, TransformationType.Trim);
        first.Configuration["Retained"] = "first";
        var second = Rule(4, TransformationType.FindAndReplace);
        second.Configuration["Find"] = "a";
        second.Configuration["Replace"] = "b";
        var third = Rule(9, TransformationType.SetDefaultValue);
        third.Configuration["Value"] = "unknown";
        var pipeline = Pipeline(rules: [first, second, third]);
        pipeline.Description = "Keep me";
        pipeline.ValidationRules = [new ValidationRule { Id = Guid.NewGuid(), Field = "name" }];
        var repository = new RecordingRepository(pipeline);

        var reordered = await CreateService(repository).ReorderAsync(
            pipeline.Id,
            [third.Id, first.Id, second.Id],
            CancellationToken.None);

        Assert.True(reordered);
        var persisted = repository.UpdatedPipeline!;
        Assert.Equal(Now, persisted.UpdatedAt);
        Assert.Equal("Keep me", persisted.Description);
        Assert.Same(pipeline.ValidationRules, persisted.ValidationRules);
        Assert.Equal(1, persisted.TransformationRules.Single(rule => rule.Id == third.Id).Order);
        Assert.Equal(2, persisted.TransformationRules.Single(rule => rule.Id == first.Id).Order);
        Assert.Equal(3, persisted.TransformationRules.Single(rule => rule.Id == second.Id).Order);
        Assert.Equal("first", persisted.TransformationRules.Single(rule => rule.Id == first.Id).Configuration["Retained"]);
        Assert.Equal("a", persisted.TransformationRules.Single(rule => rule.Id == second.Id).Configuration["Find"]);
        Assert.Equal("b", persisted.TransformationRules.Single(rule => rule.Id == second.Id).Configuration["Replace"]);
        Assert.Equal("unknown", persisted.TransformationRules.Single(rule => rule.Id == third.Id).Configuration["Value"]);

        var reloaded = await repository.GetByIdAsync(pipeline.Id, CancellationToken.None);
        Assert.Equal(
            [third.Id, first.Id, second.Id],
            reloaded!.TransformationRules.OrderBy(rule => rule.Order).Select(rule => rule.Id));
    }

    [Fact]
    public async Task ReorderAsync_RejectsDuplicateForeignAndIncompleteRuleSetsWithoutPersisting()
    {
        var first = Rule(1, TransformationType.Trim);
        var second = Rule(2, TransformationType.ToLower);
        var pipeline = Pipeline(rules: [first, second]);
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        foreach (var sequence in new IReadOnlyList<Guid>[]
        {
            [first.Id, first.Id],
            [first.Id, Guid.NewGuid()],
            [first.Id],
            [first.Id, Guid.Empty]
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReorderAsync(
                pipeline.Id,
                sequence,
                CancellationToken.None));
            Assert.Null(repository.UpdatedPipeline);
            Assert.Equal([1, 2], pipeline.TransformationRules.Select(rule => rule.Order));
        }
    }

    [Fact]
    public async Task ReorderAsync_ZeroAndOneRuleSequencesAreValidNoOpsAndMissingPipelineReturnsFalse()
    {
        var emptyPipeline = Pipeline();
        var singleRule = Rule(20, TransformationType.Trim);
        var singlePipeline = Pipeline(rules: [singleRule]);
        var repository = new RecordingRepository(emptyPipeline, singlePipeline);
        var service = CreateService(repository);

        Assert.True(await service.ReorderAsync(emptyPipeline.Id, [], CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
        Assert.True(await service.ReorderAsync(singlePipeline.Id, [singleRule.Id], CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
        Assert.False(await service.ReorderAsync(Guid.NewGuid(), [], CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidFieldAndDuplicateExistingOrders()
    {
        var pipeline = Pipeline(rules: [Rule(1, TransformationType.Trim), Rule(1, TransformationType.ToLower)]);
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(TransformationType.Trim, "missing", null, null, null),
            CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task CreateAsync_RejectsOrderOverflowWithoutPersisting()
    {
        var pipeline = Pipeline(rules: [Rule(int.MaxValue, TransformationType.Trim)]);
        var repository = new RecordingRepository(pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(repository).CreateAsync(
            pipeline.Id,
            new TransformationRuleInput(TransformationType.ToLower, "name", null, null, null),
            CancellationToken.None));

        Assert.Null(repository.UpdatedPipeline);
    }

    private static TransformationRuleService CreateService(RecordingRepository repository) =>
        new(repository, new FixedTimeProvider(Now));

    private static PipelineDefinition Pipeline(List<TransformationRule>? rules = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Customer import",
        CreatedAt = Now.AddDays(-1),
        UpdatedAt = Now.AddHours(-1),
        FieldMappings = [new FieldMapping { SourceField = "Name", TargetField = "name", IsIncluded = true }],
        TransformationRules = rules ?? []
    };

    private static TransformationRule Rule(int order, TransformationType type) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        Type = type,
        SourceField = "name"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository : IPipelineDefinitionRepository
    {
        private readonly Dictionary<Guid, PipelineDefinition> _pipelines;

        public RecordingRepository(params PipelineDefinition[] pipelines)
        {
            _pipelines = pipelines.ToDictionary(pipeline => pipeline.Id);
        }

        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public void ResetUpdate() => UpdatedPipeline = null;

        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            _pipelines.TryGetValue(id, out var pipeline);
            return Task.FromResult(pipeline);
        }

        public Task<bool> UpdateAsync(PipelineDefinition replacement, CancellationToken cancellationToken)
        {
            UpdatedPipeline = replacement;
            _pipelines[replacement.Id] = replacement;
            return Task.FromResult(true);
        }
    }
}
