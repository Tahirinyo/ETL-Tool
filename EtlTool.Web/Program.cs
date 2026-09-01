using EtlTool.Application.Connections;
using EtlTool.Application.Pipelines;
using EtlTool.Application.PostgreSql;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Application.Reporting;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Uploads;
using EtlTool.Application.Sources;
using EtlTool.Application.Mapping;
using EtlTool.Application.Loading;
using EtlTool.Application.MongoDB;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Sources;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.PostgreSql;
using EtlTool.Infrastructure.Connections;
using EtlTool.Infrastructure.Reporting;
using EtlTool.Infrastructure.Storage;
using EtlTool.Infrastructure.Uploads;
using EtlTool.Web.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

var savedConnectionProtectionOptions = builder.Configuration
    .GetSection(SavedConnectionProtectionOptions.SectionName)
    .Get<SavedConnectionProtectionOptions>()
    ?? new SavedConnectionProtectionOptions();
var savedConnectionKeyRingPath = savedConnectionProtectionOptions.ResolveKeyRingPath(
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(savedConnectionKeyRingPath);
builder.Services.AddDataProtection()
    .SetApplicationName("EtlTool.SavedDatabaseConnections.v1")
    .PersistKeysToFileSystem(new DirectoryInfo(savedConnectionKeyRingPath));

var mongoDbOptions = builder.Configuration
    .GetRequiredSection(MongoDbOptions.SectionName)
    .Get<MongoDbOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{MongoDbOptions.SectionName}' is invalid.");

mongoDbOptions.Validate();

var uploadStorageOptions = builder.Configuration
    .GetRequiredSection(UploadStorageOptions.SectionName)
    .Get<UploadStorageOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{UploadStorageOptions.SectionName}' is invalid.");

uploadStorageOptions.RootPath = uploadStorageOptions.ResolveRootPath(
    builder.Environment.ContentRootPath,
    builder.Environment.WebRootPath);
uploadStorageOptions.Validate();

var uploadValidationOptions = builder.Configuration
    .GetRequiredSection(UploadValidationOptions.SectionName)
    .Get<UploadValidationOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{UploadValidationOptions.SectionName}' is invalid.");

uploadValidationOptions.Validate();

var errorReportStorageOptions = builder.Configuration
    .GetRequiredSection(ErrorReportStorageOptions.SectionName)
    .Get<ErrorReportStorageOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{ErrorReportStorageOptions.SectionName}' is invalid.");

errorReportStorageOptions.RootPath = errorReportStorageOptions.ResolveRootPath(
    builder.Environment.ContentRootPath,
    builder.Environment.WebRootPath);
errorReportStorageOptions.Validate();
StorageRootIsolation.EnsureSeparate(
    uploadStorageOptions.RootPath,
    errorReportStorageOptions.RootPath);

var batchExecutionOptions = builder.Configuration
    .GetRequiredSection(BatchExecutionOptions.SectionName)
    .Get<BatchExecutionOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{BatchExecutionOptions.SectionName}' is invalid.");

batchExecutionOptions.Validate();

var backgroundJobQueueOptions = builder.Configuration
    .GetRequiredSection(BackgroundJobQueueOptions.SectionName)
    .Get<BackgroundJobQueueOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{BackgroundJobQueueOptions.SectionName}' is invalid.");

backgroundJobQueueOptions.Validate();

var runAdmissionOptions = builder.Configuration
    .GetRequiredSection(RunAdmissionOptions.SectionName)
    .Get<RunAdmissionOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{RunAdmissionOptions.SectionName}' is invalid.");

runAdmissionOptions.Validate();

var postgreSqlConnectionOptions = builder.Configuration
    .GetSection(PostgreSqlConnectionOptions.SectionName)
    .Get<PostgreSqlConnectionOptions>()
    ?? new PostgreSqlConnectionOptions();

postgreSqlConnectionOptions.Validate();

builder.Services.AddSingleton(mongoDbOptions);
builder.Services.AddSingleton(postgreSqlConnectionOptions);
builder.Services.AddSingleton<IPostgreSqlConnectionFactory, PostgreSqlConnectionFactory>();
builder.Services.AddSingleton<IPostgreSqlConnectionProfileCatalog>(provider =>
    (PostgreSqlConnectionFactory)provider.GetRequiredService<IPostgreSqlConnectionFactory>());
builder.Services.AddSingleton<PostgreSqlMetadataDiscoveryService>();
builder.Services.AddSingleton<IPostgreSqlMetadataDiscoveryService>(provider =>
    provider.GetRequiredService<PostgreSqlMetadataDiscoveryService>());
builder.Services.AddSingleton<IPostgreSqlDestinationAccessService>(provider =>
    provider.GetRequiredService<PostgreSqlMetadataDiscoveryService>());
builder.Services.AddSingleton<PostgreSqlSourceSchemaConverter>();
builder.Services.AddSingleton<PostgreSqlDeterministicOrderingResolver>();
builder.Services.AddSingleton<MongoMetadataDatabase>();
builder.Services.AddSingleton<IConnectionConfigurationProtector,
    DataProtectionConnectionConfigurationProtector>();
builder.Services.AddSingleton<MongoSavedDatabaseConnectionRepository>();
builder.Services.AddSingleton<ISavedDatabaseConnectionRepository>(provider =>
    provider.GetRequiredService<MongoSavedDatabaseConnectionRepository>());
builder.Services.AddSingleton<ISavedConnectionRuntimeResolver>(provider =>
    provider.GetRequiredService<MongoSavedDatabaseConnectionRepository>());
builder.Services.AddSingleton<ISavedConnectionConfigurationValidator,
    SavedConnectionConfigurationValidator>();
builder.Services.AddSingleton<ISavedConnectionReferenceChecker,
    MongoSavedConnectionReferenceChecker>();
builder.Services.AddSingleton<SavedConnectionProviderFactory>();
builder.Services.AddScoped<ISavedConnectionMetadataDiscoveryService,
    SavedConnectionMetadataDiscoveryService>();
builder.Services.AddSingleton<IMongoTargetAccessService, MongoTargetAccessService>();
builder.Services.AddSingleton<IMongoSourceMetadataDiscoveryService, MongoSourceMetadataDiscoveryService>();
builder.Services.AddSingleton<IMongoSourceSchemaInferenceService, MongoSourceSchemaInferenceService>();
builder.Services.AddSingleton<IPipelineDefinitionRepository, MongoPipelineDefinitionRepository>();
builder.Services.AddSingleton<IEtlRunRepository, MongoEtlRunRepository>();
builder.Services.AddSingleton<IDataLoader, MongoBulkUpsertLoader>();
builder.Services.AddSingleton<IDataLoader, PostgreSqlBatchUpsertLoader>();
builder.Services.AddSingleton<IDataLoaderResolver, DataLoaderResolver>();
builder.Services.AddSingleton<IErrorReportWriter, CsvErrorReportWriter>();
builder.Services.AddSingleton(errorReportStorageOptions);
builder.Services.AddSingleton<IErrorReportStore, LocalErrorReportStore>();
builder.Services.AddSingleton(uploadStorageOptions);
builder.Services.AddSingleton<IUploadStorage, LocalUploadStorage>();
builder.Services.AddSingleton<LocalRunSourceFileStore>();
builder.Services.AddSingleton<IRunSourceStore, RunSourceStore>();
builder.Services.AddSingleton<AbandonedRunRecoveryService>();
builder.Services.AddSingleton(uploadValidationOptions);
builder.Services.AddSingleton(batchExecutionOptions);
builder.Services.AddSingleton(backgroundJobQueueOptions);
builder.Services.AddSingleton(runAdmissionOptions);
builder.Services.AddSingleton<IExecutionCancellationRegistry, ExecutionCancellationRegistry>();
builder.Services.AddSingleton<InProcessBackgroundJobQueue>();
builder.Services.AddSingleton<IBackgroundJobQueue>(provider =>
    provider.GetRequiredService<InProcessBackgroundJobQueue>());
builder.Services.AddSingleton<CsvFileExtractor>();
builder.Services.AddSingleton<XlsxFileExtractor>();
builder.Services.AddSingleton<IFileExtractor>(provider => provider.GetRequiredService<CsvFileExtractor>());
builder.Services.AddSingleton<IFileExtractor>(provider => provider.GetRequiredService<XlsxFileExtractor>());
builder.Services.AddSingleton<IFileExtractorResolver, FileExtractorResolver>();
builder.Services.AddSingleton<IUploadValidationService, UploadValidationService>();
builder.Services.AddSingleton<SourceSchemaInferenceService>();
builder.Services.AddSingleton<SourceSchemaComparisonService>();
builder.Services.AddSingleton<FieldMappingService>();
builder.Services.AddSingleton<ITransformationHandler, TrimTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ToUpperTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ToLowerTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ConvertToStringTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ConvertToIntegerTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ConvertToDecimalTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ConvertToDateTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, ConditionalFilterTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, DefaultValueTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, FindAndReplaceTransformationHandler>();
builder.Services.AddSingleton<ITransformationHandler, DeduplicateTransformationHandler>();
builder.Services.AddSingleton<TransformationHandlerRegistry>();
builder.Services.AddSingleton<TransformationEngine>();
builder.Services.AddSingleton<IValidationHandler, RequiredValidationHandler>();
builder.Services.AddSingleton<IValidationHandler, EmailValidationHandler>();
builder.Services.AddSingleton<IValidationHandler, NumericRangeValidationHandler>();
builder.Services.AddSingleton<IValidationHandler, TextLengthValidationHandler>();
builder.Services.AddSingleton<IValidationHandler, DateRangeValidationHandler>();
builder.Services.AddSingleton<IValidationHandler, UpsertKeyValidationHandler>();
builder.Services.AddSingleton<ValidationHandlerRegistry>();
builder.Services.AddSingleton<ValidationEngine>();
builder.Services.AddSingleton<PipelineRowProcessor>();
builder.Services.AddScoped<ITransformationRuleService, TransformationRuleService>();
builder.Services.AddScoped<IValidationRuleService, ValidationRuleService>();
builder.Services.AddSingleton<SourceInspectionService>();
builder.Services.AddSingleton<ISourceInspectionService>(provider => provider.GetRequiredService<SourceInspectionService>());
builder.Services.AddSingleton<IWizardSourceStore>(provider => provider.GetRequiredService<SourceInspectionService>());
builder.Services.AddSingleton<IPreviewSourceFactory, PreviewSourceFactory>();
builder.Services.AddSingleton<PipelineSourceCommitCoordinator>();
builder.Services.AddHostedService<SourceInspectionCleanupService>();
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IPipelineService, PipelineService>();
builder.Services.AddScoped<SavedDatabaseConnectionService>();
builder.Services.AddScoped<ISavedDatabaseConnectionService>(provider =>
    provider.GetRequiredService<SavedDatabaseConnectionService>());
builder.Services.AddScoped<ISavedConnectionRevisionResolver>(provider =>
    provider.GetRequiredService<SavedDatabaseConnectionService>());
builder.Services.AddScoped<IPipelineReadinessService, PipelineReadinessService>();
builder.Services.AddScoped<IPreviewService, PreviewService>();
builder.Services.AddScoped<IBatchOrchestrator, BatchOrchestrator>();
builder.Services.AddScoped<IRunAdmissionService, RunAdmissionService>();
builder.Services.AddScoped<IBackgroundJobExecutor, EtlRunBackgroundJobExecutor>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pipelines}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
