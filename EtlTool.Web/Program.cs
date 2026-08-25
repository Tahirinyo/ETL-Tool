using EtlTool.Application.Pipelines;
using EtlTool.Application.Preview;
using EtlTool.Application.Processing;
using EtlTool.Application.Execution;
using EtlTool.Application.Extraction;
using EtlTool.Application.Uploads;
using EtlTool.Application.Sources;
using EtlTool.Application.Mapping;
using EtlTool.Application.Transformations;
using EtlTool.Application.Validations;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Sources;
using EtlTool.Infrastructure.MongoDB;
using EtlTool.Infrastructure.Uploads;
using EtlTool.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

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

var batchExecutionOptions = builder.Configuration
    .GetRequiredSection(BatchExecutionOptions.SectionName)
    .Get<BatchExecutionOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{BatchExecutionOptions.SectionName}' is invalid.");

batchExecutionOptions.Validate();

builder.Services.AddSingleton(mongoDbOptions);
builder.Services.AddSingleton<MongoMetadataDatabase>();
builder.Services.AddSingleton<IPipelineDefinitionRepository, MongoPipelineDefinitionRepository>();
builder.Services.AddSingleton(uploadStorageOptions);
builder.Services.AddSingleton<IUploadStorage, LocalUploadStorage>();
builder.Services.AddSingleton(uploadValidationOptions);
builder.Services.AddSingleton(batchExecutionOptions);
builder.Services.AddSingleton<CsvFileExtractor>();
builder.Services.AddSingleton<XlsxFileExtractor>();
builder.Services.AddSingleton<IFileExtractor>(provider => provider.GetRequiredService<CsvFileExtractor>());
builder.Services.AddSingleton<IFileExtractor>(provider => provider.GetRequiredService<XlsxFileExtractor>());
builder.Services.AddSingleton<IFileExtractorResolver, FileExtractorResolver>();
builder.Services.AddSingleton<IUploadValidationService, UploadValidationService>();
builder.Services.AddSingleton<SourceSchemaInferenceService>();
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
builder.Services.AddSingleton<PipelineSourceCommitCoordinator>();
builder.Services.AddHostedService<SourceInspectionCleanupService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IPipelineService, PipelineService>();
builder.Services.AddScoped<IPipelineReadinessService, PipelineReadinessService>();
builder.Services.AddScoped<IPreviewService, PreviewService>();
builder.Services.AddScoped<IBatchOrchestrator, BatchOrchestrator>();

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
