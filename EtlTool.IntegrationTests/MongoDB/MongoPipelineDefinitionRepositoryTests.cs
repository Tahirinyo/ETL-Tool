using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoPipelineDefinitionRepositoryTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task AddAndGetAsync_RoundTripsCompleteNestedPipelineInMetadataDatabase()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var pipeline = CreateCompletePipeline();

        await testDatabase.Repository.AddAsync(pipeline, CancellationToken.None);

        var persisted = await testDatabase.Repository.GetByIdAsync(
            pipeline.Id,
            CancellationToken.None);

        Assert.NotNull(persisted);
        AssertPipelineEqual(pipeline, persisted);

        var documents = await testDatabase.Database
            .GetCollection<BsonDocument>(MongoMetadataCollectionNames.PipelineDefinitions)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync();
        var document = Assert.Single(documents);
        Assert.False(document.ToJson().Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));

        var databaseNames = await (await testDatabase.Client.ListDatabaseNamesAsync()).ToListAsync();
        Assert.Contains(testDatabase.DatabaseName, databaseNames);
        Assert.DoesNotContain(pipeline.DestinationDatabase, databaseNames);
    }

    [Fact]
    public async Task AddAndGetAsync_RoundTripsPostgreSqlSourceMetadataWithoutCredentials()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var pipeline = CreateMinimalPipeline("PostgreSQL source");
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };

        await testDatabase.Repository.AddAsync(pipeline, CancellationToken.None);

        var persisted = await testDatabase.Repository.GetByIdAsync(
            pipeline.Id,
            CancellationToken.None);
        var document = await testDatabase.Database
            .GetCollection<BsonDocument>(MongoMetadataCollectionNames.PipelineDefinitions)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .SingleAsync();

        Assert.NotNull(persisted);
        Assert.NotNull(persisted.PostgreSqlSource);
        Assert.Equal("ReportingDb", persisted.PostgreSqlSource.ConnectionProfile);
        Assert.Equal("reporting", persisted.PostgreSqlSource.Database);
        Assert.Equal("public", persisted.PostgreSqlSource.Schema);
        Assert.Equal("customers", persisted.PostgreSqlSource.Table);
        Assert.DoesNotContain("ConnectionString", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", document.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAndGetAsync_RoundTripsMongoDbSourceMetadataWithoutCredentials()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var pipeline = CreateMinimalPipeline("MongoDB source");
        pipeline.SourceType = SourceType.MongoDb;
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            Database = "reporting",
            Collection = "customers"
        };

        await testDatabase.Repository.AddAsync(pipeline, CancellationToken.None);

        var persisted = await testDatabase.Repository.GetByIdAsync(
            pipeline.Id,
            CancellationToken.None);
        var document = await testDatabase.Database
            .GetCollection<BsonDocument>(MongoMetadataCollectionNames.PipelineDefinitions)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .SingleAsync();

        Assert.NotNull(persisted);
        Assert.Equal(SourceType.MongoDb, persisted.SourceType);
        Assert.NotNull(persisted.MongoDbSource);
        Assert.Equal("reporting", persisted.MongoDbSource.Database);
        Assert.Equal("customers", persisted.MongoDbSource.Collection);
        Assert.DoesNotContain("ConnectionString", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", document.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", document.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyThenMaterializesAllPipelinesWithoutOrderingContract()
    {
        await using var testDatabase = fixture.CreateDatabase();

        Assert.Empty(await testDatabase.Repository.ListAsync(CancellationToken.None));

        var first = CreateMinimalPipeline("First");
        var second = CreateMinimalPipeline("Second");
        await testDatabase.Repository.AddAsync(first, CancellationToken.None);
        await testDatabase.Repository.AddAsync(second, CancellationToken.None);

        var pipelines = await testDatabase.Repository.ListAsync(CancellationToken.None);

        Assert.Equal(2, pipelines.Count);
        Assert.Equal(
            new HashSet<Guid> { first.Id, second.Id },
            pipelines.Select(pipeline => pipeline.Id).ToHashSet());
    }

    [Fact]
    public async Task UpdateAsync_ReplacesCompleteAggregateAndReportsIdenticalReplacementAsMatched()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var original = CreateCompletePipeline();
        await testDatabase.Repository.AddAsync(original, CancellationToken.None);

        var replacement = CreateMinimalPipeline("Replacement");
        replacement.Id = original.Id;
        replacement.CreatedAt = original.CreatedAt;
        replacement.UpdatedAt = original.UpdatedAt.AddMinutes(10);

        var firstResult = await testDatabase.Repository.UpdateAsync(
            replacement,
            CancellationToken.None);
        var secondResult = await testDatabase.Repository.UpdateAsync(
            replacement,
            CancellationToken.None);
        var persisted = await testDatabase.Repository.GetByIdAsync(
            original.Id,
            CancellationToken.None);

        Assert.True(firstResult);
        Assert.True(secondResult);
        Assert.NotNull(persisted);
        AssertPipelineEqual(replacement, persisted);
        Assert.Empty(persisted.TransformationRules);
        Assert.Empty(persisted.ValidationRules);
    }

    [Fact]
    public async Task UpdateAsync_MissingPipelineReturnsFalseAndDoesNotUpsert()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var missing = CreateCompletePipeline();

        var updated = await testDatabase.Repository.UpdateAsync(
            missing,
            CancellationToken.None);

        Assert.False(updated);
        Assert.Null(await testDatabase.Repository.GetByIdAsync(
            missing.Id,
            CancellationToken.None));
        Assert.Empty(await testDatabase.Repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_ExistingThenMissingReturnsTrueThenFalse()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var pipeline = CreateMinimalPipeline("Delete me");
        await testDatabase.Repository.AddAsync(pipeline, CancellationToken.None);

        var firstResult = await testDatabase.Repository.DeleteAsync(
            pipeline.Id,
            CancellationToken.None);
        var secondResult = await testDatabase.Repository.DeleteAsync(
            pipeline.Id,
            CancellationToken.None);

        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Null(await testDatabase.Repository.GetByIdAsync(
            pipeline.Id,
            CancellationToken.None));
    }

    [Fact]
    public async Task GetByIdAsync_MissingPipelineReturnsNull()
    {
        await using var testDatabase = fixture.CreateDatabase();

        var result = await testDatabase.Repository.GetByIdAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddAsync_DuplicateIdThrowsPipelineSpecificException()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var first = CreateMinimalPipeline("First");
        var duplicate = CreateMinimalPipeline("Duplicate");
        duplicate.Id = first.Id;
        await testDatabase.Repository.AddAsync(first, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DuplicatePipelineDefinitionException>(
            () => testDatabase.Repository.AddAsync(duplicate, CancellationToken.None));
        var persisted = await testDatabase.Repository.GetByIdAsync(
            first.Id,
            CancellationToken.None);

        Assert.Equal(first.Id, exception.PipelineId);
        Assert.IsType<MongoWriteException>(exception.InnerException);
        Assert.NotNull(persisted);
        AssertPipelineEqual(first, persisted);
    }

    [Fact]
    public async Task AddAsync_DuplicateNonIdIndexPreservesMongoWriteException()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var collection = testDatabase.Database.GetCollection<PipelineDefinition>(
            MongoMetadataCollectionNames.PipelineDefinitions);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<PipelineDefinition>(
                Builders<PipelineDefinition>.IndexKeys.Ascending(pipeline => pipeline.Name),
                new CreateIndexOptions { Unique = true }));

        var first = CreateMinimalPipeline("Same name");
        var duplicateName = CreateMinimalPipeline("Same name");
        await testDatabase.Repository.AddAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<MongoWriteException>(
            () => testDatabase.Repository.AddAsync(duplicateName, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_AlreadyCancelledTokenRemainsCancellation()
    {
        await using var testDatabase = fixture.CreateDatabase();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => testDatabase.Repository.ListAsync(cancellationSource.Token));
    }

    private static PipelineDefinition CreateCompletePipeline()
    {
        return new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Customer import",
            Description = "Complete nested metadata",
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions
            {
                CultureName = "tr-TR",
                DateFormat = "dd.MM.yyyy",
                Delimiter = CsvDelimiter.Semicolon,
                WorksheetName = "Customers",
                FirstRowIsHeader = true
            },
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "CustomerId", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "CustomerId", TargetField = "customer_id" },
                new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = false }
            ],
            TransformationRules =
            [
                new TransformationRule
                {
                    Id = Guid.NewGuid(),
                    Type = TransformationType.Trim,
                    Order = 1,
                    SourceField = "email",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Value"] = "first",
                        ["value"] = "second"
                    }
                },
                new TransformationRule
                {
                    Id = Guid.NewGuid(),
                    Type = TransformationType.ToLower,
                    Order = 2,
                    SourceField = "email"
                },
                new TransformationRule
                {
                    Id = Guid.NewGuid(),
                    Type = TransformationType.Deduplicate,
                    Order = 3,
                    SourceField = null,
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Fields"] = "[\"customer_id\",\"email\"]"
                    }
                }
            ],
            ValidationRules =
            [
                new ValidationRule
                {
                    Id = Guid.NewGuid(),
                    Type = ValidationType.EmailFormat,
                    Field = "email",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AllowEmpty"] = "false"
                    },
                    ErrorMessage = "Email is invalid."
                }
            ],
            DestinationDatabase = $"destination_{Guid.NewGuid():N}",
            DestinationCollection = "customers",
            UpsertKeyField = "customer_id",
            CreatedAt = new DateTimeOffset(2026, 8, 17, 9, 30, 0, TimeSpan.FromHours(3)),
            UpdatedAt = new DateTimeOffset(2026, 8, 17, 10, 45, 0, TimeSpan.FromHours(3))
        };
    }

    private static PipelineDefinition CreateMinimalPipeline(string name)
    {
        return new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = null,
            SourceType = SourceType.Xlsx,
            SourceOptions = new SourceOptions
            {
                CultureName = "en-US",
                DateFormat = null,
                Delimiter = null,
                WorksheetName = null,
                FirstRowIsHeader = true
            },
            DestinationDatabase = "etl_destination",
            DestinationCollection = "records",
            UpsertKeyField = "id",
            CreatedAt = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero)
        };
    }

    private static void AssertPipelineEqual(
        PipelineDefinition expected,
        PipelineDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.SourceType, actual.SourceType);
        Assert.Equal(expected.SourceOptions.CultureName, actual.SourceOptions.CultureName);
        Assert.Equal(expected.SourceOptions.DateFormat, actual.SourceOptions.DateFormat);
        Assert.Equal(expected.SourceOptions.Delimiter, actual.SourceOptions.Delimiter);
        Assert.Equal(expected.SourceOptions.WorksheetName, actual.SourceOptions.WorksheetName);
        Assert.Equal(expected.SourceOptions.FirstRowIsHeader, actual.SourceOptions.FirstRowIsHeader);

        Assert.Equal(expected.ExpectedSchema.Count, actual.ExpectedSchema.Count);
        for (var index = 0; index < expected.ExpectedSchema.Count; index++)
        {
            Assert.Equal(expected.ExpectedSchema[index].Name, actual.ExpectedSchema[index].Name);
            Assert.Equal(expected.ExpectedSchema[index].DataType, actual.ExpectedSchema[index].DataType);
        }

        Assert.Equal(expected.FieldMappings.Count, actual.FieldMappings.Count);
        for (var index = 0; index < expected.FieldMappings.Count; index++)
        {
            Assert.Equal(expected.FieldMappings[index].SourceField, actual.FieldMappings[index].SourceField);
            Assert.Equal(expected.FieldMappings[index].TargetField, actual.FieldMappings[index].TargetField);
            Assert.Equal(expected.FieldMappings[index].IsIncluded, actual.FieldMappings[index].IsIncluded);
        }

        Assert.Equal(expected.TransformationRules.Count, actual.TransformationRules.Count);
        for (var index = 0; index < expected.TransformationRules.Count; index++)
        {
            var expectedRule = expected.TransformationRules[index];
            var actualRule = actual.TransformationRules[index];
            Assert.Equal(expectedRule.Id, actualRule.Id);
            Assert.Equal(expectedRule.Type, actualRule.Type);
            Assert.Equal(expectedRule.Order, actualRule.Order);
            Assert.Equal(expectedRule.SourceField, actualRule.SourceField);
            AssertDictionaryEqual(expectedRule.Configuration, actualRule.Configuration);
        }

        Assert.Equal(expected.ValidationRules.Count, actual.ValidationRules.Count);
        for (var index = 0; index < expected.ValidationRules.Count; index++)
        {
            var expectedRule = expected.ValidationRules[index];
            var actualRule = actual.ValidationRules[index];
            Assert.Equal(expectedRule.Id, actualRule.Id);
            Assert.Equal(expectedRule.Type, actualRule.Type);
            Assert.Equal(expectedRule.Field, actualRule.Field);
            Assert.Equal(expectedRule.ErrorMessage, actualRule.ErrorMessage);
            AssertDictionaryEqual(expectedRule.Configuration, actualRule.Configuration);
        }

        Assert.Equal(expected.DestinationDatabase, actual.DestinationDatabase);
        Assert.Equal(expected.DestinationCollection, actual.DestinationCollection);
        Assert.Equal(expected.UpsertKeyField, actual.UpsertKeyField);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private static void AssertDictionaryEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        foreach (var pair in expected)
        {
            Assert.True(actual.TryGetValue(pair.Key, out var actualValue));
            Assert.Equal(pair.Value, actualValue);
        }
    }
}
