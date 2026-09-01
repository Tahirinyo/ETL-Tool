using EtlTool.Application.Pipelines;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Validations;

public sealed class ValidationRuleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ValidationType.Required)]
    [InlineData(ValidationType.EmailFormat)]
    public async Task CreateAsync_FieldRulePersistsCustomMessageAndNoConfiguration(ValidationType type)
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);

        var rule = await CreateService(repository).CreateAsync(
            pipeline.Id,
            new ValidationRuleInput(type, "email", null, null, "Use a work email."),
            CancellationToken.None);

        Assert.NotNull(rule);
        Assert.Equal("email", rule.Field);
        Assert.Equal("Use a work email.", rule.ErrorMessage);
        Assert.Empty(rule.Configuration);
        Assert.Equal(Now, repository.UpdatedPipeline!.UpdatedAt);
        Assert.Single(repository.UpdatedPipeline.ValidationRules);
        Assert.Same(pipeline.TransformationRules, repository.UpdatedPipeline.TransformationRules);
        Assert.Equal(DestinationType.PostgreSql, repository.UpdatedPipeline.DestinationType);
    }

    [Theory]
    [InlineData(ValidationType.NumericRange, "12,5", "20,5")]
    [InlineData(ValidationType.TextLengthRange, "1", "20")]
    [InlineData(ValidationType.DateRange, "01.01.2026", "31.12.2026")]
    public async Task CreateAsync_RangeRulePersistsOnlyExactBoundKeys(ValidationType type, string minimum, string maximum)
    {
        var pipeline = Pipeline();
        pipeline.SourceOptions = new SourceOptions { CultureName = "tr-TR" };
        var repository = new RecordingRepository(pipeline);

        var rule = await CreateService(repository).CreateAsync(
            pipeline.Id,
            new ValidationRuleInput(type, "amount", minimum, maximum, null),
            CancellationToken.None);

        Assert.NotNull(rule);
        Assert.Equal(["Maximum", "Minimum"], rule.Configuration.Keys.OrderBy(key => key));
        Assert.Equal(minimum, rule.Configuration["Minimum"]);
        Assert.Equal(maximum, rule.Configuration["Maximum"]);
    }

    [Fact]
    public async Task CreateAsync_AcceptsOneSidedRangesAndRejectsMalformedOrReversedBounds()
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        var maximumOnly = await service.CreateAsync(
            pipeline.Id,
            new ValidationRuleInput(ValidationType.TextLengthRange, "email", null, "50", null),
            CancellationToken.None);
        Assert.Equal(["Maximum"], maximumOnly!.Configuration.Keys);

        foreach (var input in new[]
        {
            new ValidationRuleInput(ValidationType.NumericRange, "amount", null, null, null),
            new ValidationRuleInput(ValidationType.TextLengthRange, "email", "-1", null, null),
            new ValidationRuleInput(ValidationType.DateRange, "amount", "2026-12-31", "2026-01-01", null)
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline.Id, input, CancellationToken.None));
        }
    }

    [Fact]
    public async Task CreateAsync_UpsertKeyUsesPipelineFieldAndRejectsMissingOrStaleConfiguration()
    {
        var pipeline = Pipeline();
        pipeline.UpsertKeyField = "email";
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        var created = await service.CreateAsync(
            pipeline.Id,
            new ValidationRuleInput(ValidationType.UpsertKeyRequired, "forged", "ignored", "ignored", null),
            CancellationToken.None);
        Assert.Equal("email", created!.Field);
        Assert.Empty(created.Configuration);

        var stalePipeline = Pipeline();
        stalePipeline.UpsertKeyField = "missing";
        var staleService = CreateService(new RecordingRepository(stalePipeline));
        await Assert.ThrowsAsync<ArgumentException>(() => staleService.CreateAsync(
            stalePipeline.Id,
            new ValidationRuleInput(ValidationType.UpsertKeyRequired, null, null, null, null), CancellationToken.None));
    }

    [Theory]
    [InlineData(ValidationType.Unspecified)]
    [InlineData((ValidationType)999)]
    public async Task CreateAsync_RejectsUnsupportedTypesAndUnknownFieldsWithoutPersistence(ValidationType type)
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            pipeline.Id, new ValidationRuleInput(type, "amount", null, null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            pipeline.Id, new ValidationRuleInput(ValidationType.Required, "missing", null, null, null), CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task UpdateAsync_ReplacesOnlyTargetRuleAtItsExistingPosition()
    {
        var first = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.Required, Field = "email", ErrorMessage = "Keep me" };
        var target = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.NumericRange, Field = "amount", Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "1" } };
        var last = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.EmailFormat, Field = "email" };
        var pipeline = Pipeline();
        pipeline.Description = "Keep me";
        pipeline.ValidationRules = [first, target, last];
        var repository = new RecordingRepository(pipeline);

        var updated = await CreateService(repository).UpdateAsync(
            pipeline.Id,
            target.Id,
            new ValidationRuleInput(ValidationType.Required, "amount", null, null, "Required amount."),
            CancellationToken.None);

        Assert.True(updated);
        var persisted = repository.UpdatedPipeline!;
        Assert.Equal([first.Id, target.Id, last.Id], persisted.ValidationRules.Select(rule => rule.Id));
        Assert.Equal(3, persisted.ValidationRules.Count);
        Assert.Equal(ValidationType.Required, persisted.ValidationRules[1].Type);
        Assert.Equal("amount", persisted.ValidationRules[1].Field);
        Assert.Empty(persisted.ValidationRules[1].Configuration);
        Assert.Equal("Required amount.", persisted.ValidationRules[1].ErrorMessage);
        Assert.Equal("Keep me", persisted.Description);
        Assert.Same(pipeline.FieldMappings, persisted.FieldMappings);
        Assert.Same(pipeline.TransformationRules, persisted.TransformationRules);
        Assert.Equal(Now, persisted.UpdatedAt);
    }

    [Theory]
    [InlineData(ValidationType.Required, "email", null, null)]
    [InlineData(ValidationType.EmailFormat, "email", null, null)]
    [InlineData(ValidationType.NumericRange, "amount", "1", "2")]
    [InlineData(ValidationType.TextLengthRange, "email", "1", "20")]
    [InlineData(ValidationType.DateRange, "amount", "2026-01-01", "2026-12-31")]
    public async Task UpdateAsync_RebuildsSupportedRuleConfiguration(ValidationType type, string field, string? minimum, string? maximum)
    {
        var existing = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.DateRange, Field = "amount", Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "2025-01-01" } };
        var pipeline = Pipeline();
        pipeline.ValidationRules = [existing];
        var repository = new RecordingRepository(pipeline);

        var updated = await CreateService(repository).UpdateAsync(
            pipeline.Id, existing.Id, new ValidationRuleInput(type, field, minimum, maximum, "Changed."), CancellationToken.None);

        Assert.True(updated);
        var rule = Assert.Single(repository.UpdatedPipeline!.ValidationRules);
        Assert.Equal(existing.Id, rule.Id);
        Assert.Equal(type, rule.Type);
        Assert.Equal(field, rule.Field);
        Assert.Equal("Changed.", rule.ErrorMessage);
        if (minimum is null) Assert.Empty(rule.Configuration);
        else Assert.Equal(minimum, rule.Configuration["Minimum"]);
        if (maximum is not null) Assert.Equal(maximum, rule.Configuration["Maximum"]);
    }

    [Fact]
    public async Task UpdateAsync_UpsertKeyUsesCurrentPipelineFieldAndRejectsStaleStateWithoutPersistence()
    {
        var existing = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.Required, Field = "email" };
        var pipeline = Pipeline();
        pipeline.ValidationRules = [existing];
        pipeline.UpsertKeyField = "amount";
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        Assert.True(await service.UpdateAsync(pipeline.Id, existing.Id,
            new ValidationRuleInput(ValidationType.UpsertKeyRequired, "forged", "ignored", "ignored", null), CancellationToken.None));
        Assert.Equal("amount", Assert.Single(repository.UpdatedPipeline!.ValidationRules).Field);

        pipeline.FieldMappings.Clear();
        repository.ResetUpdate();
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(pipeline.Id, existing.Id,
            new ValidationRuleInput(ValidationType.Required, "email", null, null, null), CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task UpdateAsync_UpsertKeyRevalidatesCurrentKeyConfiguration()
    {
        var existing = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.Required, Field = "email" };
        var pipeline = Pipeline();
        pipeline.ValidationRules = [existing];
        pipeline.UpsertKeyField = "email";
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        pipeline.UpsertKeyField = "amount";
        Assert.True(await service.UpdateAsync(pipeline.Id, existing.Id,
            new ValidationRuleInput(ValidationType.UpsertKeyRequired, "email", null, null, null), CancellationToken.None));
        Assert.Equal("amount", Assert.Single(repository.UpdatedPipeline!.ValidationRules).Field);

        pipeline.UpsertKeyField = "missing";
        repository.SetPipeline(pipeline);
        repository.ResetUpdate();
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(pipeline.Id, existing.Id,
            new ValidationRuleInput(ValidationType.UpsertKeyRequired, "forged", null, null, null), CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);

        pipeline.UpsertKeyField = string.Empty;
        repository.SetPipeline(pipeline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(pipeline.Id, existing.Id,
            new ValidationRuleInput(ValidationType.UpsertKeyRequired, "forged", null, null, null), CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsFalseForMissingPipelineOrRuleAndRejectsEmptyRuleId()
    {
        var pipeline = Pipeline();
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);
        var input = new ValidationRuleInput(ValidationType.Required, "email", null, null, null);

        Assert.False(await service.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), input, CancellationToken.None));
        Assert.False(await service.UpdateAsync(pipeline.Id, Guid.NewGuid(), input, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(pipeline.Id, Guid.Empty, input, CancellationToken.None));
        Assert.Null(repository.UpdatedPipeline);
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidOrForgedInputWithoutPersistence()
    {
        var existing = new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.Required, Field = "email" };
        var pipeline = Pipeline();
        pipeline.ValidationRules = [existing];
        var repository = new RecordingRepository(pipeline);
        var service = CreateService(repository);

        foreach (var input in new[]
        {
            new ValidationRuleInput(ValidationType.NumericRange, "amount", "20", "10", null),
            new ValidationRuleInput(ValidationType.TextLengthRange, "email", "-1", null, null),
            new ValidationRuleInput(ValidationType.Required, "Email", null, null, null),
            new ValidationRuleInput((ValidationType)999, "email", null, null, null)
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
                pipeline.Id, existing.Id, input, CancellationToken.None));
        }

        Assert.Null(repository.UpdatedPipeline);
    }

    private static ValidationRuleService CreateService(RecordingRepository repository) => new(repository, new FixedTimeProvider(Now));

    private static PipelineDefinition Pipeline() => new()
    {
        Id = Guid.NewGuid(), Name = "Import", DestinationType = DestinationType.PostgreSql,
        CreatedAt = Now.AddDays(-1), UpdatedAt = Now.AddHours(-1),
        FieldMappings =
        [
            new() { SourceField = "Email", TargetField = "email", IsIncluded = true },
            new() { SourceField = "Amount", TargetField = "amount", IsIncluded = true }
        ],
        TransformationRules = [new() { Id = Guid.NewGuid(), Type = TransformationType.Trim, SourceField = "email" }]
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository(params PipelineDefinition[] pipelines) : IPipelineDefinitionRepository
    {
        private readonly Dictionary<Guid, PipelineDefinition> _pipelines = pipelines.ToDictionary(pipeline => pipeline.Id);
        public PipelineDefinition? UpdatedPipeline { get; private set; }
        public void ResetUpdate() => UpdatedPipeline = null;
        public void SetPipeline(PipelineDefinition pipeline) => _pipelines[pipeline.Id] = pipeline;
        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_pipelines.GetValueOrDefault(id));
        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
        {
            UpdatedPipeline = pipeline;
            _pipelines[pipeline.Id] = pipeline;
            return Task.FromResult(true);
        }
    }
}
