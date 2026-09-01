using System.Globalization;
using System.Text.Json;
using EtlTool.Application.Mapping;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;

namespace EtlTool.UnitTests.Application.Pipelines;

public sealed class PipelineReadinessServiceTests
{
    [Fact]
    public async Task EvaluateAsync_ConfiguredMongoDbSourceIsReadyForExecution()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = SourceType.MongoDb;
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            Database = "reporting",
            Collection = "customers"
        };

        var result = await EvaluateAsync(pipeline);

        Assert.True(result!.IsReady);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void EvaluateForPreview_AllowsMongoDbAndRetainsRuleValidation()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = SourceType.MongoDb;
        pipeline.MongoDbSource = new MongoDbSourceOptions
        {
            Database = "reporting",
            Collection = "customers"
        };
        pipeline.TransformationRules =
        [
            new TransformationRule { Type = TransformationType.Trim, Order = 1, SourceField = "missing" }
        ];
        var service = new PipelineReadinessService(
            new Repository(_ => throw new InvalidOperationException("Repository must not be called.")),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance);

        var result = service.EvaluateForPreview(pipeline);

        Assert.False(result.IsReady);
        Assert.DoesNotContain(result.Problems, problem =>
            problem.Component == "Source"
            && problem.Message.Contains("preview is not available", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem =>
            problem.Component == "Transformation"
            && problem.Message.Contains("'missing' is not", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SourceType.PostgreSql)]
    [InlineData(SourceType.MongoDb)]
    public void EvaluateAndEvaluateForPreview_BlockDatabaseSourceUntilRemappingIsSaved(
        SourceType sourceType)
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = sourceType;
        pipeline.RequiresRemapping = true;
        if (sourceType == SourceType.PostgreSql)
        {
            pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
            {
                ConnectionProfile = "ReportingDb", Database = "reporting", Schema = "public", Table = "customers"
            };
        }
        else
        {
            pipeline.MongoDbSource = new MongoDbSourceOptions { Database = "reporting", Collection = "customers" };
        }
        var service = new PipelineReadinessService(
            new Repository(_ => throw new InvalidOperationException("Repository must not be called.")),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance);

        var execution = service.Evaluate(pipeline);
        var preview = service.EvaluateForPreview(pipeline);

        Assert.False(execution.IsReady);
        Assert.False(preview.IsReady);
        Assert.All([execution, preview], result => Assert.Contains(result.Problems, problem =>
            problem.Component == "Mapping"
            && problem.Message.Contains("Review and save", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EvaluateAsync_ReportsMissingMongoDbSourceMetadata()
    {
        var missingConfiguration = ReadyPipeline();
        missingConfiguration.SourceType = SourceType.MongoDb;
        missingConfiguration.MongoDbSource = null;
        var incompleteConfiguration = ReadyPipeline();
        incompleteConfiguration.SourceType = SourceType.MongoDb;
        incompleteConfiguration.MongoDbSource = new MongoDbSourceOptions
        {
            Database = " ",
            Collection = string.Empty
        };

        var missingResult = await EvaluateAsync(missingConfiguration);
        var incompleteResult = await EvaluateAsync(incompleteConfiguration);

        Assert.Contains(missingResult!.Problems, problem =>
            problem.Component == "Source"
            && problem.Message.Contains("configuration is missing", StringComparison.Ordinal));
        Assert.Contains(incompleteResult!.Problems, problem =>
            problem.Component == "Source"
            && problem.Message.Contains("database is required", StringComparison.Ordinal));
        Assert.Contains(incompleteResult.Problems, problem =>
            problem.Component == "Source"
            && problem.Message.Contains("collection is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_UsesProvidedDefinitionWithoutRepositoryAccess()
    {
        var pipeline = ReadyPipeline();
        var service = new PipelineReadinessService(
            new Repository(_ => throw new InvalidOperationException("Repository must not be called.")),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance);

        var result = service.Evaluate(pipeline);

        Assert.True(result.IsReady);
        Assert.Throws<ArgumentNullException>(() => service.Evaluate(null!));
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsReadyForCompletePipeline()
    {
        var pipeline = ReadyPipeline();
        pipeline.TransformationRules =
        [
            new TransformationRule
            {
                Id = Guid.NewGuid(), Type = TransformationType.Trim, Order = 1, SourceField = "email"
            },
            new TransformationRule
            {
                Id = Guid.NewGuid(), Type = TransformationType.Deduplicate, Order = 2,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Fields"] = JsonSerializer.Serialize(new[] { "email", "amount" })
                }
            }
        ];
        pipeline.ValidationRules =
        [
            new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.Required, Field = "email" },
            new ValidationRule
            {
                Id = Guid.NewGuid(), Type = ValidationType.NumericRange, Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Minimum"] = "1,5", ["Maximum"] = "2,5"
                }
            },
            new ValidationRule
            {
                Id = Guid.NewGuid(), Type = ValidationType.DateRange, Field = "occurredAt",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Minimum"] = "01.01.2026", ["Maximum"] = "31.12.2026"
                }
            },
            new ValidationRule { Id = Guid.NewGuid(), Type = ValidationType.UpsertKeyRequired, Field = "email" }
        ];

        var result = await EvaluateAsync(pipeline);

        Assert.True(result!.IsReady);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task EvaluateAsync_ConfiguredPostgreSqlSourceIsReadyForExecution()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.SourceOptions.FirstRowIsHeader = false;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };

        var result = await EvaluateAsync(pipeline);

        Assert.True(result!.IsReady);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task EvaluateAsync_RequiresStructurallyValidPostgreSqlDestinationConfiguration()
    {
        var pipeline = ReadyPipeline();
        pipeline.DestinationType = DestinationType.PostgreSql;
        pipeline.DestinationDatabase = string.Empty;
        pipeline.DestinationCollection = string.Empty;
        pipeline.PostgreSqlDestination = new PostgreSqlDestinationOptions
        {
            ConnectionProfile = "WarehouseDb",
            Database = "warehouse",
            Schema = "import",
            Table = "customers",
            ColumnMappings =
            [
                new PostgreSqlDestinationColumnMapping { OutputField = "email", DestinationColumn = "email" }
            ],
            UpsertKeyColumn = "email"
        };

        var valid = await EvaluateAsync(pipeline);

        Assert.True(valid!.IsReady);

        pipeline.PostgreSqlDestination.ColumnMappings[0].OutputField = "missing";
        var stale = await EvaluateAsync(pipeline);

        Assert.False(stale!.IsReady);
        Assert.Contains(stale.Problems, problem => problem.Component == "Destination"
            && problem.Message.Contains("not an included mapped output field", StringComparison.Ordinal));

        pipeline.PostgreSqlDestination.ColumnMappings[0].OutputField = "email";
        pipeline.UpsertKeyField = "amount";
        var inconsistent = await EvaluateAsync(pipeline);

        Assert.Contains(inconsistent!.Problems, problem => problem.Component == "Destination"
            && problem.Message.Contains("must use the output field", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateForPreview_AndExecutionShareConfiguredPostgreSqlReadiness()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.SourceOptions.FirstRowIsHeader = false;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions
        {
            ConnectionProfile = "ReportingDb",
            Database = "reporting",
            Schema = "public",
            Table = "customers"
        };
        var service = new PipelineReadinessService(
            new Repository(_ => throw new InvalidOperationException("Repository must not be called.")),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance);

        Assert.True(service.EvaluateForPreview(pipeline).IsReady);
        Assert.True(service.Evaluate(pipeline).IsReady);
    }

    [Fact]
    public void EvaluateForPreview_RejectsMissingPostgreSqlIdentityAndRetainsRemapGuards()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = SourceType.PostgreSql;
        pipeline.PostgreSqlSource = new PostgreSqlSourceOptions { ConnectionProfile = "ReportingDb" };
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = true }
        ];
        pipeline.TransformationRules =
        [
            new TransformationRule { Type = TransformationType.Trim, Order = 1, SourceField = "amount" }
        ];
        var service = new PipelineReadinessService(
            new Repository(_ => throw new InvalidOperationException("Repository must not be called.")),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance);

        var result = service.EvaluateForPreview(pipeline);
        var executionResult = service.Evaluate(pipeline);

        Assert.False(result.IsReady);
        Assert.Contains(result.Problems, problem => problem.Message.Contains("database is required", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("schema is required", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("table is required", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem =>
            problem.Component == "Transformation"
            && problem.Message.Contains("'amount' is not", StringComparison.Ordinal));
        Assert.Equal(result.Problems, executionResult.Problems);
    }

    [Fact]
    public async Task EvaluateAsync_AggregatesIndependentProblemsInDeterministicOrder()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceOptions.FirstRowIsHeader = false;
        pipeline.FieldMappings = [new FieldMapping { SourceField = "Email", TargetField = "", IsIncluded = true }];
        pipeline.TransformationRules =
        [
            new TransformationRule { Type = TransformationType.Trim, Order = 2, SourceField = "missing" },
            new TransformationRule { Type = TransformationType.Trim, Order = 2, SourceField = "missing" },
            new TransformationRule
            {
                Type = TransformationType.FindAndReplace, Order = 3, SourceField = "missing",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Find"] = "" }
            }
        ];
        pipeline.ValidationRules =
        [
            new ValidationRule { Type = ValidationType.Required, Field = "missing" },
            new ValidationRule
            {
                Type = ValidationType.TextLengthRange, Field = "missing",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "3", ["Maximum"] = "2" }
            }
        ];
        pipeline.DestinationDatabase = " ";
        pipeline.DestinationCollection = " ";
        pipeline.UpsertKeyField = "missing";

        var result = await EvaluateAsync(pipeline);

        Assert.False(result!.IsReady);
        Assert.Equal(
            ["Source", "Mapping", "Transformation", "Transformation", "Transformation", "Transformation", "Transformation", "Validation", "Validation", "Validation", "Destination", "Destination", "Upsert key"],
            result.Problems.Select(problem => problem.Component));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("order '2'", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("Find", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("greater than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsync_ProducesEquivalentProblemOrderAcrossRepeatedEvaluations()
    {
        var pipeline = ReadyPipeline();
        pipeline.FieldMappings = [new FieldMapping { SourceField = "Email", TargetField = "", IsIncluded = true }];
        pipeline.TransformationRules = [new TransformationRule { Type = TransformationType.Trim, Order = 1, SourceField = "missing" }];
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.Required, Field = "missing" }];
        pipeline.UpsertKeyField = "missing";

        var first = await EvaluateAsync(pipeline);
        var second = await EvaluateAsync(pipeline);

        Assert.Equal(first!.Problems, second!.Problems);
    }

    [Theory]
    [InlineData("all-excluded")]
    [InlineData("duplicate-target")]
    public async Task EvaluateAsync_ReportsPersistedInvalidMappingStates(string caseName)
    {
        var pipeline = ReadyPipeline();
        pipeline.FieldMappings = caseName == "all-excluded"
            ?
            [
                new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = false },
                new FieldMapping { SourceField = "Amount", TargetField = "amount", IsIncluded = false },
                new FieldMapping { SourceField = "OccurredAt", TargetField = "occurredAt", IsIncluded = false }
            ]
            :
            [
                new FieldMapping { SourceField = "Email", TargetField = "duplicate", IsIncluded = true },
                new FieldMapping { SourceField = "Amount", TargetField = "duplicate", IsIncluded = true },
                new FieldMapping { SourceField = "OccurredAt", TargetField = "occurredAt", IsIncluded = true }
            ];

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Mapping"
            && problem.Message.Contains(
                caseName == "all-excluded" ? "At least one" : "more than one",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateAsync_ReportsUnavailableMappingsWithoutCascadingFieldReferenceProblems()
    {
        var pipeline = ReadyPipeline();
        pipeline.FieldMappings = null!;
        pipeline.TransformationRules = [new TransformationRule { Type = TransformationType.Trim, Order = 1, SourceField = "missing" }];
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.Required, Field = "missing" }];
        pipeline.UpsertKeyField = "missing";

        var result = await EvaluateAsync(pipeline);

        Assert.Equal("Mapping", Assert.Single(result!.Problems, problem => problem.Component == "Mapping").Component);
        Assert.DoesNotContain(result.Problems, problem => problem.Message.Contains("'missing' is not", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidTransformationRules))]
    public async Task EvaluateAsync_ReportsMalformedTransformationConfiguration(TransformationRule rule, string expectedMessage)
    {
        var pipeline = ReadyPipeline();
        pipeline.TransformationRules = [rule];

        var result = await EvaluateAsync(pipeline);

        var problem = Assert.Single(result!.Problems, problem => problem.Component == "Transformation");
        Assert.Contains(expectedMessage, problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsStaleDeduplicationFields()
    {
        var pipeline = ReadyPipeline();
        pipeline.TransformationRules =
        [
            new TransformationRule
            {
                Type = TransformationType.Deduplicate,
                Order = 1,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Fields"] = JsonSerializer.Serialize(new[] { "email", "Email" })
                }
            }
        ];

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Transformation"
            && problem.Message.Contains("'Email' is not", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TransformationType.Trim)]
    [InlineData(TransformationType.ConvertToDecimal)]
    [InlineData(TransformationType.FilterRow)]
    public async Task EvaluateAsync_ReportsExcludedTransformationSourceFields(TransformationType type)
    {
        var pipeline = ReadyPipeline();
        pipeline.FieldMappings.Single(mapping => mapping.TargetField == "amount").IsIncluded = false;
        var rule = new TransformationRule { Type = type, Order = 1, SourceField = "amount" };
        if (type == TransformationType.FilterRow)
        {
            rule.Configuration["Operator"] = FilterOperator.Equals.ToString();
            rule.Configuration["Value"] = "1";
        }
        pipeline.TransformationRules = [rule];

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Transformation"
            && problem.Message.Contains("'amount' is not", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidValidationRules))]
    public async Task EvaluateAsync_ReportsMalformedValidationConfiguration(ValidationRule rule, string expectedMessage)
    {
        var pipeline = ReadyPipeline();
        pipeline.ValidationRules = [rule];

        var result = await EvaluateAsync(pipeline);

        var problem = Assert.Single(result!.Problems, problem => problem.Component == "Validation");
        Assert.Contains(expectedMessage, problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ValidationRulesWithStaleFields))]
    public async Task EvaluateAsync_ReportsStaleFieldsForEveryValidationFamily(ValidationRule rule)
    {
        var pipeline = ReadyPipeline();
        rule.Field = "Amount";
        pipeline.ValidationRules = [rule];

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Validation"
            && problem.Message.Contains("'Amount' is not", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ValidOneSidedRangeRules))]
    public async Task EvaluateAsync_AcceptsRuntimeCompatibleOneSidedRangeConfiguration(ValidationRule rule)
    {
        var pipeline = ReadyPipeline();
        pipeline.ValidationRules = [rule];

        var result = await EvaluateAsync(pipeline);

        Assert.True(result!.IsReady);
    }

    [Fact]
    public async Task EvaluateAsync_UsesPipelineCultureAndDateFormatInsteadOfCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            var pipeline = ReadyPipeline();
            pipeline.SourceOptions = new SourceOptions
            {
                CultureName = "en-US", Delimiter = CsvDelimiter.Comma, FirstRowIsHeader = true, DateFormat = "MM/dd/yyyy"
            };
            pipeline.ValidationRules =
            [
                new ValidationRule
                {
                    Type = ValidationType.NumericRange, Field = "amount",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Minimum"] = "1,234.5", ["Maximum"] = "1,234.5"
                    }
                },
                new ValidationRule
                {
                    Type = ValidationType.DateRange, Field = "occurredAt",
                    Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Minimum"] = "12/31/2026", ["Maximum"] = "12/31/2026"
                    }
                }
            ];

            var result = await EvaluateAsync(pipeline);

            Assert.True(result!.IsReady);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task EvaluateAsync_ReportsSourceAndDestinationConfigurationFailures()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceType = SourceType.Xlsx;
        pipeline.SourceOptions = new SourceOptions
        {
            CultureName = "en-US", FirstRowIsHeader = false, WorksheetName = "", DateFormat = "MM"
        };
        pipeline.DestinationDatabase = " ";
        pipeline.DestinationCollection = " ";

        var result = await EvaluateAsync(pipeline);

        Assert.Equal(3, result!.Problems.Count(problem => problem.Component == "Source"));
        Assert.Equal(2, result.Problems.Count(problem => problem.Component == "Destination"));
    }

    [Theory]
    [InlineData("The configured destination database cannot be used as an ETL target.")]
    [InlineData("The configured MongoDB destination name is not valid.")]
    public void Evaluate_ReportsTargetPolicyRejectionAsDestinationConfigurationFailure(
        string rejectionMessage)
    {
        var pipeline = ReadyPipeline();
        var service = new PipelineReadinessService(
            new Repository(_ => throw new InvalidOperationException("Repository must not be called.")),
            new FieldMappingService(),
            new RejectedTargetAccessService(rejectionMessage));

        var result = service.Evaluate(pipeline);

        var problem = Assert.Single(result.Problems, problem => problem.Component == "Destination");
        Assert.Equal(rejectionMessage, problem.Message);
    }

    [Theory]
    [InlineData("yyyy'")]
    [InlineData("yyyy%")]
    [InlineData("yyyy-MM-ddz")]
    [InlineData("yyyy-MM-ddzz")]
    [InlineData("yyyy-MM-ddzzz")]
    [InlineData("yyyy-MM-ddK")]
    public async Task EvaluateAsync_ReportsSourceProblemForDateFormatsTheSharedParserRejects(
        string dateFormat)
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceOptions.DateFormat = dateFormat;

        var result = await EvaluateAsync(pipeline);

        Assert.False(result!.IsReady);
        Assert.Contains(result.Problems, problem => problem.Component == "Source");
    }

    [Theory]
    [InlineData("yyyy-MM-dd 'zK'")]
    [InlineData(@"yyyy-MM-dd \z\K")]
    public async Task EvaluateAsync_AcceptsDateFormatsWithTimezoneLikeLiterals(string dateFormat)
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceOptions.DateFormat = dateFormat;

        var result = await EvaluateAsync(pipeline);

        Assert.True(result!.IsReady);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsUnsupportedPersistedValidationTypeAndInvalidCulture()
    {
        var pipeline = ReadyPipeline();
        pipeline.SourceOptions.CultureName = "not-a-culture";
        pipeline.ValidationRules = [new ValidationRule { Type = (ValidationType)999, Field = "email" }];

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Source"
            && problem.Message.Contains("source culture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Problems, problem =>
            problem.Component == "Validation"
            && problem.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateAsync_ReportsOptionalUpsertRuleOnlyWhenItIsInconsistent()
    {
        var pipeline = ReadyPipeline();
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.UpsertKeyRequired, Field = "amount" }];

        var result = await EvaluateAsync(pipeline);

        Assert.False(result!.IsReady);
        Assert.Contains(result.Problems, problem => problem.Message.Contains("must target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsync_ReportsStalePipelineAndValidationUpsertKeyReferencesSeparately()
    {
        var pipeline = ReadyPipeline();
        pipeline.UpsertKeyField = "missing";
        pipeline.ValidationRules = [new ValidationRule { Type = ValidationType.UpsertKeyRequired, Field = "missing" }];

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Validation"
            && problem.Message.Contains("'missing' is not", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem =>
            problem.Component == "Upsert key"
            && problem.Message.Contains("'missing' is not", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsync_RevalidatesPersistedReferencesAfterSuccessfulMappingReplacement()
    {
        var pipeline = ReadyPipeline();
        pipeline.ExpectedSchema.Add(new SourceFieldDefinition
        {
            Name = "Legacy", DataType = SourceFieldType.String
        });
        pipeline.FieldMappings.Add(new FieldMapping
        {
            SourceField = "Legacy", TargetField = "legacy", IsIncluded = true
        });
        pipeline.TransformationRules =
        [
            new TransformationRule
            {
                Type = TransformationType.Trim, Order = 1, SourceField = "email"
            },
            new TransformationRule
            {
                Type = TransformationType.Trim, Order = 2, SourceField = "legacy"
            },
            new TransformationRule
            {
                Type = TransformationType.Deduplicate,
                Order = 3,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Fields"] = JsonSerializer.Serialize(new[] { "email", "legacy" })
                }
            }
        ];
        pipeline.ValidationRules =
        [
            new ValidationRule { Type = ValidationType.Required, Field = "email" },
            new ValidationRule { Type = ValidationType.Required, Field = "legacy" },
            new ValidationRule { Type = ValidationType.UpsertKeyRequired, Field = "legacy" }
        ];
        pipeline.UpsertKeyField = "legacy";

        Assert.True((await EvaluateAsync(pipeline))!.IsReady);

        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Replacement", DataType = SourceFieldType.String }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = true },
            new FieldMapping { SourceField = "Replacement", TargetField = "replacement", IsIncluded = true }
        ];

        var stale = await EvaluateAsync(pipeline);

        Assert.False(stale!.IsReady);
        Assert.Equal(
            ["Transformation", "Transformation", "Validation", "Validation", "Upsert key"],
            stale.Problems.Select(problem => problem.Component));
        Assert.Equal(2, stale.Problems.Count(problem =>
            problem.Component == "Transformation"
            && problem.Message.Contains("'legacy' is not", StringComparison.Ordinal)));
        Assert.Equal(2, stale.Problems.Count(problem =>
            problem.Component == "Validation"
            && problem.Message.Contains("'legacy' is not", StringComparison.Ordinal)));
        Assert.Contains(stale.Problems, problem =>
            problem.Component == "Upsert key"
            && problem.Message.Contains("'legacy' is not", StringComparison.Ordinal));
        Assert.DoesNotContain(stale.Problems, problem =>
            problem.Message.Contains("'email' is not", StringComparison.Ordinal));
        Assert.Equal("legacy", pipeline.TransformationRules[1].SourceField);
        Assert.Equal("legacy", pipeline.ValidationRules[1].Field);
        Assert.Equal("legacy", pipeline.UpsertKeyField);

        pipeline.FieldMappings[1].TargetField = "legacy";

        var repaired = await EvaluateAsync(pipeline);

        Assert.True(repaired!.IsReady);

        pipeline.FieldMappings[1].TargetField = "replacement";
        pipeline.TransformationRules[1].SourceField = "replacement";
        pipeline.TransformationRules[2].Configuration["Fields"] =
            JsonSerializer.Serialize(new[] { "email", "replacement" });
        pipeline.ValidationRules[1].Field = "replacement";
        pipeline.ValidationRules[2].Field = "replacement";
        pipeline.UpsertKeyField = "replacement";

        var referencesRepaired = await EvaluateAsync(pipeline);

        Assert.True(referencesRepaired!.IsReady);
    }

    [Fact]
    public async Task EvaluateAsync_RevalidatesExcludedAndCaseDistinctDeduplicationFieldsAfterRemap()
    {
        var pipeline = ReadyPipeline();
        pipeline.ExpectedSchema.Add(new SourceFieldDefinition
        {
            Name = "Legacy", DataType = SourceFieldType.String
        });
        pipeline.FieldMappings.Add(new FieldMapping
        {
            SourceField = "Legacy", TargetField = "legacy", IsIncluded = true
        });
        pipeline.TransformationRules =
        [
            new TransformationRule
            {
                Type = TransformationType.Deduplicate,
                Order = 1,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Fields"] = JsonSerializer.Serialize(new[] { "email", "legacy" })
                }
            }
        ];

        Assert.True((await EvaluateAsync(pipeline))!.IsReady);

        pipeline.ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Replacement", DataType = SourceFieldType.String }
        ];
        pipeline.FieldMappings =
        [
            new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = true },
            new FieldMapping { SourceField = "Replacement", TargetField = "legacy", IsIncluded = false }
        ];
        pipeline.TransformationRules[0].Configuration["Fields"] =
            JsonSerializer.Serialize(new[] { "email", "legacy", "Legacy" });

        var result = await EvaluateAsync(pipeline);

        Assert.False(result!.IsReady);
        Assert.Equal(["Transformation", "Transformation"], result.Problems.Select(problem => problem.Component));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("'legacy' is not", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Message.Contains("'Legacy' is not", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsync_ReportsMissingPipelineUpsertKey()
    {
        var pipeline = ReadyPipeline();
        pipeline.UpsertKeyField = " ";

        var result = await EvaluateAsync(pipeline);

        Assert.Contains(result!.Problems, problem =>
            problem.Component == "Upsert key"
            && problem.Message.Contains("must be an included", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotMutateTheSavedPipeline()
    {
        var pipeline = ReadyPipeline();
        var mapping = pipeline.FieldMappings[0];
        var rule = new TransformationRule
        {
            Type = TransformationType.Deduplicate,
            Order = 1,
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fields"] = JsonSerializer.Serialize(new[] { "email" })
            }
        };
        pipeline.TransformationRules = [rule];
        var originalConfiguration = rule.Configuration.ToArray();

        _ = await EvaluateAsync(pipeline);

        Assert.Same(mapping, pipeline.FieldMappings[0]);
        Assert.Same(rule, pipeline.TransformationRules[0]);
        Assert.Equal(originalConfiguration, rule.Configuration.ToArray());
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotRequireTemporaryUploadState()
    {
        var pipeline = ReadyPipeline();

        var result = await EvaluateAsync(pipeline);

        Assert.True(result!.IsReady);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNullForMissingPipeline()
    {
        var result = await EvaluateAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task EvaluateAsync_PropagatesCancellationAndRepositoryExceptions()
    {
        var cancelled = new Repository(_ => Task.FromCanceled<PipelineDefinition?>(new CancellationToken(canceled: true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PipelineReadinessService(cancelled, new FieldMappingService(), AllowedTargetAccessService.Instance).EvaluateAsync(Guid.NewGuid(), CancellationToken.None));

        var failing = new Repository(_ => Task.FromException<PipelineDefinition?>(new InvalidOperationException("Repository unavailable.")));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PipelineReadinessService(failing, new FieldMappingService(), AllowedTargetAccessService.Instance).EvaluateAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("Repository unavailable.", exception.Message);
    }

    public static TheoryData<TransformationRule, string> InvalidTransformationRules => new()
    {
        {
            new TransformationRule
            {
                Type = TransformationType.SetDefaultValue, Order = 1, SourceField = "email",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            },
            "Value"
        },
        {
            new TransformationRule
            {
                Type = TransformationType.FindAndReplace, Order = 1, SourceField = "email",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Find"] = "x" }
            },
            "Replace"
        },
        {
            new TransformationRule
            {
                Type = TransformationType.FilterRow, Order = 1, SourceField = "email",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Operator"] = "equals", ["Value"] = "x" }
            },
            "Operator"
        },
        {
            new TransformationRule
            {
                Type = TransformationType.Deduplicate, Order = 1,
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Fields"] = "not-json" }
            },
            "JSON"
        }
    };

    public static TheoryData<ValidationRule, string> InvalidValidationRules => new()
    {
        {
            new ValidationRule
            {
                Type = ValidationType.NumericRange, Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["minimum"] = "1" }
            },
            "exact ordinal"
        },
        {
            new ValidationRule
            {
                Type = ValidationType.TextLengthRange, Field = "email",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "3", ["Maximum"] = "2" }
            },
            "greater than"
        },
        {
            new ValidationRule
            {
                Type = ValidationType.DateRange, Field = "occurredAt",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "31.12.2026", ["Maximum"] = "30.12.2026" }
            },
            "later than"
        }
    };

    public static TheoryData<ValidationRule> ValidationRulesWithStaleFields => new()
    {
        { new ValidationRule { Type = ValidationType.Required, Field = "amount" } },
        { new ValidationRule { Type = ValidationType.EmailFormat, Field = "amount" } },
        {
            new ValidationRule
            {
                Type = ValidationType.NumericRange, Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "1" }
            }
        },
        {
            new ValidationRule
            {
                Type = ValidationType.TextLengthRange, Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "1" }
            }
        },
        {
            new ValidationRule
            {
                Type = ValidationType.DateRange, Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "01.01.2026" }
            }
        }
    };

    public static TheoryData<ValidationRule> ValidOneSidedRangeRules => new()
    {
        {
            new ValidationRule
            {
                Type = ValidationType.NumericRange, Field = "amount",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "1,5" }
            }
        },
        {
            new ValidationRule
            {
                Type = ValidationType.TextLengthRange, Field = "email",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Maximum"] = "100" }
            }
        },
        {
            new ValidationRule
            {
                Type = ValidationType.DateRange, Field = "occurredAt",
                Configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["Minimum"] = "01.01.2026" }
            }
        }
    };

    private static async Task<PipelineReadinessResult?> EvaluateAsync(PipelineDefinition? pipeline)
    {
        var id = pipeline?.Id ?? Guid.NewGuid();
        return await new PipelineReadinessService(
            new Repository(requestedId => Task.FromResult(requestedId == id ? pipeline : null)),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance).EvaluateAsync(id, CancellationToken.None);
    }

    private static async Task<PipelineReadinessResult?> EvaluateAsync(Guid id)
    {
        return await new PipelineReadinessService(
            new Repository(_ => Task.FromResult<PipelineDefinition?>(null)),
            new FieldMappingService(),
            AllowedTargetAccessService.Instance).EvaluateAsync(id, CancellationToken.None);
    }

    private static PipelineDefinition ReadyPipeline() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Customer import",
        SourceType = SourceType.Csv,
        SourceOptions = new SourceOptions
        {
            CultureName = "tr-TR", Delimiter = CsvDelimiter.Semicolon, FirstRowIsHeader = true, DateFormat = "dd.MM.yyyy"
        },
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String },
            new SourceFieldDefinition { Name = "Amount", DataType = SourceFieldType.Decimal },
            new SourceFieldDefinition { Name = "OccurredAt", DataType = SourceFieldType.Date }
        ],
        FieldMappings =
        [
            new FieldMapping { SourceField = "Email", TargetField = "email", IsIncluded = true },
            new FieldMapping { SourceField = "Amount", TargetField = "amount", IsIncluded = true },
            new FieldMapping { SourceField = "OccurredAt", TargetField = "occurredAt", IsIncluded = true }
        ],
        DestinationDatabase = "demo",
        DestinationCollection = "customers",
        UpsertKeyField = "email"
    };

    private sealed class Repository(Func<Guid, Task<PipelineDefinition?>> getById) : IPipelineDefinitionRepository
    {
        public Task<PipelineDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => getById(id);

        public Task AddAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AllowedTargetAccessService : IMongoTargetAccessService
    {
        public static AllowedTargetAccessService Instance { get; } = new();

        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Allowed;

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnsureUpsertIndexAsync(
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RejectedTargetAccessService(string rejectionMessage) : IMongoTargetAccessService
    {
        public MongoTargetValidationResult Validate(MongoTarget target) =>
            MongoTargetValidationResult.Rejected(rejectionMessage);

        public Task EnsureAccessibleAsync(MongoTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureUpsertIndexAsync(
            MongoTarget target,
            string upsertKeyField,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
