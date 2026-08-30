using System.Text;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Processing;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.MongoDB;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EtlTool.IntegrationTests.MongoDB;

public sealed class MongoTargetAccessServiceValidationTests
{
    [Fact]
    public void Validate_AllowsOrdinaryTargetWithoutExistingCollection()
    {
        var result = CreateUnreachableService().Validate(
            new MongoTarget("etl_tool_target_safety_tests", "customers"));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task EnsureAccessibleAsync_UnreachableServerRaisesRunLevelAccessFailure()
    {
        var service = CreateUnreachableService();

        var exception = await Assert.ThrowsAsync<MongoTargetAccessException>(() =>
            service.EnsureAccessibleAsync(
                new MongoTarget("etl_tool_target_safety_tests", "customers"),
                CancellationToken.None));

        Assert.Equal("The configured MongoDB destination could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task EnsureAccessibleAsync_PropagatesCancellation()
    {
        var service = CreateUnreachableService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.EnsureAccessibleAsync(
                new MongoTarget("etl_tool_target_safety_tests", "customers"),
                cancellation.Token));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("config")]
    [InlineData("CONFIG")]
    [InlineData("local")]
    [InlineData("LoCaL")]
    public void Validate_RejectsMongoSystemDatabase(string databaseName)
    {
        var result = CreateUnreachableService().Validate(new MongoTarget(databaseName, "customers"));

        Assert.False(result.IsAllowed);
        Assert.Equal("The configured destination database cannot be used as an ETL target.", result.FailureMessage);
    }

    [Theory]
    [InlineData("etl_tool_metadata")]
    [InlineData("ETL_TOOL_METADATA")]
    public void Validate_RejectsConfiguredMetadataDatabase(string databaseName)
    {
        var result = CreateUnreachableService().Validate(
            new MongoTarget(databaseName, "customers"));

        Assert.False(result.IsAllowed);
        Assert.Equal("The configured destination database cannot be used as an ETL target.", result.FailureMessage);
    }

    [Theory]
    [InlineData("bad/database", "customers")]
    [InlineData("bad\\database", "customers")]
    [InlineData("bad.database", "customers")]
    [InlineData("bad database", "customers")]
    [InlineData("bad\"database", "customers")]
    [InlineData("bad$database", "customers")]
    [InlineData("etl_tool_target_safety_tests", "bad$collection")]
    [InlineData("etl_tool_target_safety_tests", "system.profile")]
    [InlineData("etl_tool_target_safety_tests", "archive.system.rows")]
    public void Validate_RejectsStaticallyInvalidServerNames(
        string databaseName,
        string collectionName)
    {
        var result = CreateUnreachableService().Validate(
            new MongoTarget(databaseName, collectionName));

        Assert.False(result.IsAllowed);
        Assert.Equal("The configured MongoDB destination name is not valid.", result.FailureMessage);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(16)]
    public void Validate_RejectsDatabaseNameAtOrAbove64Utf8Bytes(int characterCount)
    {
        var databaseName = characterCount == 64
            ? new string('a', characterCount)
            : string.Concat(Enumerable.Repeat("\U0001F600", characterCount));

        var result = CreateUnreachableService().Validate(
            new MongoTarget(databaseName, "customers"));

        Assert.Equal(64, System.Text.Encoding.UTF8.GetByteCount(databaseName));
        Assert.False(result.IsAllowed);
        Assert.Equal("The configured MongoDB destination name is not valid.", result.FailureMessage);
    }

    [Theory]
    [InlineData("Customer-Data", "2026_import")]
    [InlineData("data:archive", "customers.with.periods")]
    [InlineData("etl_tool_target_safety_tests", "System.profile")]
    public void Validate_AllowsNamesMongoDbDoesNotUniversallyProhibit(
        string databaseName,
        string collectionName)
    {
        var result = CreateUnreachableService().Validate(
            new MongoTarget(databaseName, collectionName));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Validate_Allows63ByteDatabaseName()
    {
        var databaseName = $"{new string('a', 3)}{string.Concat(Enumerable.Repeat("\U0001F600", 15))}";

        var result = CreateUnreachableService().Validate(
            new MongoTarget(databaseName, "customers"));

        Assert.Equal(63, System.Text.Encoding.UTF8.GetByteCount(databaseName));
        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("config")]
    [InlineData("CONFIG")]
    [InlineData("local")]
    [InlineData("LoCaL")]
    [InlineData("etl_tool_metadata")]
    [InlineData("ETL_TOOL_METADATA")]
    public void Readiness_ClassifiesProtectedDatabaseCasingAsDestinationFailure(
        string databaseName)
    {
        var pipeline = ReadyPipeline(databaseName, "customers");
        var result = ReadinessService().Evaluate(pipeline);

        var problem = Assert.Single(
            result.Problems,
            problem => problem.Component == "Destination");
        Assert.Equal(
            "The configured destination database cannot be used as an ETL target.",
            problem.Message);
    }

    [Theory]
    [InlineData("bad/database", "customers")]
    [InlineData("etl_tool_target_safety_tests", "bad$collection")]
    [InlineData("etl_tool_target_safety_tests", "system.profile")]
    public void Readiness_ClassifiesInvalidNamespaceAsDestinationFailure(
        string databaseName,
        string collectionName)
    {
        var pipeline = ReadyPipeline(databaseName, collectionName);
        var result = ReadinessService().Evaluate(pipeline);

        var problem = Assert.Single(
            result.Problems,
            problem => problem.Component == "Destination");
        Assert.Equal("The configured MongoDB destination name is not valid.", problem.Message);
    }

    [Theory]
    [InlineData("bad\0database", "customers")]
    [InlineData("etl_tool_target_safety_tests", "bad\0collection")]
    public void Validate_RejectsNamesRejectedByMongoDriver(
        string databaseName,
        string collectionName)
    {
        var result = CreateUnreachableService().Validate(
            new MongoTarget(databaseName, collectionName));

        Assert.False(result.IsAllowed);
        Assert.Equal("The configured MongoDB destination name is not valid.", result.FailureMessage);
    }

    private static MongoTargetAccessService CreateUnreachableService()
    {
        var options = new MongoDbOptions
        {
            ConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
            MetadataDatabaseName = "etl_tool_metadata"
        };
        return new MongoTargetAccessService(new MongoMetadataDatabase(options), options);
    }

    private static PipelineReadinessService ReadinessService() => new(
        new NullPipelineRepository(),
        new FieldMappingService(),
        CreateUnreachableService());

    private static PipelineDefinition ReadyPipeline(
        string databaseName,
        string collectionName) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Target validation",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.String }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true }
        ],
        DestinationDatabase = databaseName,
        DestinationCollection = collectionName,
        UpsertKeyField = "id"
    };

    private sealed class NullPipelineRepository : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

[Collection(MongoDbTestCollection.CollectionName)]
public sealed class MongoTargetAccessServiceIntegrationTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task EnsureAccessibleAsync_AllowsNonexistentTargetForBroadCredential()
    {
        await using var testDatabase = fixture.CreateDatabase();
        var target = new MongoTarget(
            $"etl_tool_target_safety_{Guid.NewGuid():N}",
            "customers");

        var validation = testDatabase.TargetAccessService.Validate(target);
        await testDatabase.TargetAccessService.EnsureAccessibleAsync(target, CancellationToken.None);

        Assert.True(validation.IsAllowed);
    }

    [Fact]
    public async Task EnsureAccessibleAsync_AllowsCollectionScopedCredentialWithoutListCollectionsPrivilege()
    {
        await using var testDatabase = fixture.CreateDatabase();
        const string collectionName = "authorized_customers";

        await testDatabase.Database.CreateCollectionAsync(collectionName);
        var service = await CreateCollectionScopedServiceAsync(testDatabase, collectionName);

        await service.EnsureAccessibleAsync(
            new MongoTarget(testDatabase.DatabaseName, collectionName),
            CancellationToken.None);
    }

    [Fact]
    public async Task EnsureAccessibleAsync_RejectsExistingCollectionOutsideCollectionScopedCredential()
    {
        await using var testDatabase = fixture.CreateDatabase();
        const string authorizedCollection = "authorized_customers";
        const string unauthorizedCollection = "unauthorized_customers";

        await testDatabase.Database.CreateCollectionAsync(authorizedCollection);
        await testDatabase.Database.CreateCollectionAsync(unauthorizedCollection);
        var service = await CreateCollectionScopedServiceAsync(
            testDatabase,
            authorizedCollection);

        var exception = await Assert.ThrowsAsync<MongoTargetAccessException>(() =>
            service.EnsureAccessibleAsync(
                new MongoTarget(testDatabase.DatabaseName, unauthorizedCollection),
                CancellationToken.None));

        Assert.Equal("The configured MongoDB destination could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task EnsureAccessibleAsync_RejectsNonexistentTargetForCollectionScopedCredential()
    {
        await using var testDatabase = fixture.CreateDatabase();
        const string authorizedCollection = "authorized_customers";
        const string nonexistentCollection = "future_customers";

        await testDatabase.Database.CreateCollectionAsync(authorizedCollection);
        var service = await CreateCollectionScopedServiceAsync(
            testDatabase,
            authorizedCollection);

        var exception = await Assert.ThrowsAsync<MongoTargetAccessException>(() =>
            service.EnsureAccessibleAsync(
                new MongoTarget(testDatabase.DatabaseName, nonexistentCollection),
                CancellationToken.None));

        Assert.Equal("The configured MongoDB destination could not be accessed.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCollectionScopedDeniedTargetBeforeExtractionOrBatchCallback()
    {
        await using var testDatabase = fixture.CreateDatabase();
        const string authorizedCollection = "authorized_customers";
        const string unauthorizedCollection = "unauthorized_customers";

        await testDatabase.Database.CreateCollectionAsync(authorizedCollection);
        await testDatabase.Database.CreateCollectionAsync(unauthorizedCollection);
        var targetAccessService = await CreateCollectionScopedServiceAsync(
            testDatabase,
            authorizedCollection);
        var resolver = new TrackingResolver(new CsvFileExtractor());
        var mappingService = new FieldMappingService();
        var orchestrator = new BatchOrchestrator(
            resolver,
            new PipelineReadinessService(
                new ThrowingPipelineRepository(),
                mappingService,
                targetAccessService),
            new PipelineRowProcessor(
                mappingService,
                new TransformationEngine(new TransformationHandlerRegistry([])),
                new ValidationEngine(new ValidationHandlerRegistry([]))),
            targetAccessService,
            new BatchExecutionOptions { BatchSize = 1 });
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("Id\n1\n"));
        var callbackInvocations = 0;

        await Assert.ThrowsAsync<MongoTargetAccessException>(() =>
            orchestrator.ExecuteAsync(
                source,
                ReadyPipeline(testDatabase.DatabaseName, unauthorizedCollection),
                (_, _) =>
                {
                    callbackInvocations++;
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, callbackInvocations);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsIndexPermissionFailureBeforeExtractionOrBatchCallback()
    {
        await using var testDatabase = fixture.CreateDatabase();
        const string collectionName = "read_only_customers";

        await testDatabase.Database.CreateCollectionAsync(collectionName);
        var targetAccessService = await CreateCollectionScopedServiceAsync(
            testDatabase,
            collectionName);
        var resolver = new TrackingResolver(new CsvFileExtractor());
        var mappingService = new FieldMappingService();
        var orchestrator = new BatchOrchestrator(
            resolver,
            new PipelineReadinessService(
                new ThrowingPipelineRepository(),
                mappingService,
                targetAccessService),
            new PipelineRowProcessor(
                mappingService,
                new TransformationEngine(new TransformationHandlerRegistry([])),
                new ValidationEngine(new ValidationHandlerRegistry([]))),
            targetAccessService,
            new BatchExecutionOptions { BatchSize = 1 });
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("Id\n1\n"));
        var callbackInvocations = 0;

        await Assert.ThrowsAsync<MongoTargetAccessException>(() =>
            orchestrator.ExecuteAsync(
                source,
                ReadyPipeline(testDatabase.DatabaseName, collectionName),
                (_, _) =>
                {
                    callbackInvocations++;
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal(0, resolver.InvocationCount);
        Assert.Equal(0, callbackInvocations);
    }

    private static async Task<MongoTargetAccessService> CreateCollectionScopedServiceAsync(
        MongoDbTestDatabase testDatabase,
        string collectionName)
    {
        var roleName = $"target_reader_{Guid.NewGuid():N}";
        var username = $"target_user_{Guid.NewGuid():N}";
        const string password = "test-only-password";

        await testDatabase.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "createRole", roleName },
            {
                "privileges",
                new BsonArray
                {
                    new BsonDocument
                    {
                        {
                            "resource",
                            new BsonDocument
                            {
                                { "db", testDatabase.DatabaseName },
                                { "collection", collectionName }
                            }
                        },
                        { "actions", new BsonArray { "find" } }
                    }
                }
            },
            { "roles", new BsonArray() }
        });
        await testDatabase.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "createUser", username },
            { "pwd", password },
            { "roles", new BsonArray { roleName } }
        });

        var connection = new MongoUrlBuilder(testDatabase.ConnectionString)
        {
            Username = username,
            Password = password,
            AuthenticationSource = testDatabase.DatabaseName
        }.ToString();
        var options = new MongoDbOptions
        {
            ConnectionString = connection,
            MetadataDatabaseName = "etl_tool_metadata"
        };
        return new MongoTargetAccessService(
            new MongoMetadataDatabase(options),
            options);
    }

    private static PipelineDefinition ReadyPipeline(
        string databaseName,
        string collectionName) => new()
    {
        Id = Guid.NewGuid(),
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.String }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Id", TargetField = "id", IsIncluded = true }
        ],
        DestinationDatabase = databaseName,
        DestinationCollection = collectionName,
        UpsertKeyField = "id"
    };

    private sealed class TrackingResolver(IFileExtractor extractor) : IFileExtractorResolver
    {
        public int InvocationCount { get; private set; }

        public IFileExtractor Resolve(SourceType sourceType)
        {
            InvocationCount++;
            Assert.Equal(extractor.SourceType, sourceType);
            return extractor;
        }
    }

    private sealed class ThrowingPipelineRepository : IPipelineDefinitionRepository
    {
        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
