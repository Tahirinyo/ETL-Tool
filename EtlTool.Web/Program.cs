using EtlTool.Application.Pipelines;
using EtlTool.Application.Uploads;
using EtlTool.Application.Sources;
using EtlTool.Application.Mapping;
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

builder.Services.AddSingleton(mongoDbOptions);
builder.Services.AddSingleton<MongoMetadataDatabase>();
builder.Services.AddSingleton<IPipelineDefinitionRepository, MongoPipelineDefinitionRepository>();
builder.Services.AddSingleton(uploadStorageOptions);
builder.Services.AddSingleton<IUploadStorage, LocalUploadStorage>();
builder.Services.AddSingleton(uploadValidationOptions);
builder.Services.AddSingleton<CsvFileExtractor>();
builder.Services.AddSingleton<XlsxFileExtractor>();
builder.Services.AddSingleton<IUploadValidationService, UploadValidationService>();
builder.Services.AddSingleton<SourceSchemaInferenceService>();
builder.Services.AddSingleton<FieldMappingService>();
builder.Services.AddSingleton<SourceInspectionService>();
builder.Services.AddSingleton<ISourceInspectionService>(provider => provider.GetRequiredService<SourceInspectionService>());
builder.Services.AddHostedService<SourceInspectionCleanupService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IPipelineService, PipelineService>();

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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pipelines}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
