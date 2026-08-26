using System.Net;
using System.Text.Json;
using EtlTool.Application.Execution;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Web.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EtlTool.IntegrationTests.Web;

public sealed class RunsControllerRoutingTests
{
    [Fact]
    public async Task StatusRoute_ReturnsCamelCaseJsonAndRejectsNonGuidIdentifiers()
    {
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            Status = EtlRunStatus.Running,
            StartedAt = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 8, 25, 10, 3, 0, TimeSpan.Zero),
            TotalRows = 10,
            ProcessedRows = 4,
            ValidRows = 1,
            InvalidRows = 1,
            FilteredRows = 1,
            DeduplicatedRows = 1,
            InsertedRows = 1,
            UpdatedRows = 0,
            PipelineName = "Internal metadata",
            StoredFilePath = "runs/source.csv",
            SystemError = "Internal diagnostic.",
            ErrorReportPath = "reports/errors.csv"
        };
        await using var host = await RunStatusHost.StartAsync(new InMemoryRunRepository(run));

        using var success = await host.Client.GetAsync($"/Runs/{run.Id}/Status");
        using var document = JsonDocument.Parse(await success.Content.ReadAsStreamAsync());
        using var invalid = await host.Client.GetAsync("/Runs/not-a-guid/Status");

        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(run.Id, document.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal("Running", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(run.StartedAt, document.RootElement.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(run.CompletedAt, document.RootElement.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(10, document.RootElement.GetProperty("totalRows").GetInt64());
        Assert.Equal(4, document.RootElement.GetProperty("processedRows").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("validRows").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("invalidRows").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("filteredRows").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("deduplicatedRows").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("insertedRows").GetInt64());
        Assert.Equal(0, document.RootElement.GetProperty("updatedRows").GetInt64());
        Assert.Equal(12, document.RootElement.EnumerateObject().Count());
        Assert.False(document.RootElement.TryGetProperty("pipelineName", out _));
        Assert.False(document.RootElement.TryGetProperty("storedFilePath", out _));
        Assert.False(document.RootElement.TryGetProperty("errorReportPath", out _));
        Assert.False(document.RootElement.TryGetProperty("systemError", out _));
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
    }

    private sealed class RunStatusHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private RunStatusHost(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<RunStatusHost> StartAsync(IEtlRunRepository repository)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddControllersWithViews().AddApplicationPart(typeof(RunsController).Assembly);
            builder.Services.AddSingleton(repository);

            var application = builder.Build();
            application.MapControllers();
            await application.StartAsync();

            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new RunStatusHost(application, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class InMemoryRunRepository(EtlRun run) : IEtlRunRepository
    {
        public Task<EtlRun?> GetByIdAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<EtlRun?>(runId == run.Id ? run : null);

        public Task AddAsync(EtlRun value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryStartAsync(Guid runId, DateTimeOffset startedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryUpdateProgressAsync(Guid runId, BatchExecutionProgress progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryMarkTerminalAsync(Guid runId, EtlRunStatus status, DateTimeOffset completedAt, BatchExecutionProgress? finalProgress, string? systemError, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
