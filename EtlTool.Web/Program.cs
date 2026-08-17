using EtlTool.Application.Pipelines;
using EtlTool.Infrastructure.MongoDB;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

var mongoDbOptions = builder.Configuration
    .GetRequiredSection(MongoDbOptions.SectionName)
    .Get<MongoDbOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{MongoDbOptions.SectionName}' is invalid.");

mongoDbOptions.Validate();

builder.Services.AddSingleton(mongoDbOptions);
builder.Services.AddSingleton<MongoMetadataDatabase>();
builder.Services.AddSingleton<IPipelineDefinitionRepository, MongoPipelineDefinitionRepository>();
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
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
