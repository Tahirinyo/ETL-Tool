using System.Net;
using EtlTool.Application.Pipelines;
using EtlTool.Domain.Entities;
using EtlTool.Web.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EtlTool.IntegrationTests.Web;

public sealed class PipelinesControllerBindingTests
{
    [Fact]
    public async Task EditPost_UsesRouteIdWhenFormContainsConflictingId()
    {
        var routePipeline = CreatePipeline("Route pipeline");
        var postedPipeline = CreatePipeline("Posted pipeline");
        var service = new RecordingPipelineService(routePipeline, postedPipeline);
        await using var host = await BindingTestHost.StartAsync(service);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();

        using var response = await host.Client.PostAsync(
            $"/Pipelines/Edit/{routePipeline.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = postedPipeline.Id.ToString(),
                ["Name"] = "Updated route pipeline",
                ["Description"] = "Route identity remained authoritative",
                ["__RequestVerificationToken"] = antiForgeryToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(routePipeline.Id, service.UpdatedId);
        Assert.Equal("Updated route pipeline", service.GetRequired(routePipeline.Id).Name);
        Assert.Equal("Posted pipeline", service.GetRequired(postedPipeline.Id).Name);
    }

    [Fact]
    public async Task DeletePost_UsesRouteIdWhenFormContainsConflictingId()
    {
        var routePipeline = CreatePipeline("Route pipeline");
        var postedPipeline = CreatePipeline("Posted pipeline");
        var service = new RecordingPipelineService(routePipeline, postedPipeline);
        await using var host = await BindingTestHost.StartAsync(service);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();

        using var response = await host.Client.PostAsync(
            $"/Pipelines/Delete/{routePipeline.Id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = postedPipeline.Id.ToString(),
                ["__RequestVerificationToken"] = antiForgeryToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(routePipeline.Id, service.DeletedId);
        Assert.False(service.Contains(routePipeline.Id));
        Assert.True(service.Contains(postedPipeline.Id));
    }

    private static PipelineDefinition CreatePipeline(string name)
    {
        return new PipelineDefinition
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }

    private sealed class BindingTestHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private BindingTestHost(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<BindingTestHost> StartAsync(IPipelineService pipelineService)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services
                .AddControllersWithViews()
                .AddApplicationPart(typeof(PipelinesController).Assembly);
            builder.Services.AddSingleton(pipelineService);

            var application = builder.Build();
            application.MapGet(
                "/antiforgery-token",
                (HttpContext context, IAntiforgery antiForgery) =>
                {
                    var tokens = antiForgery.GetAndStoreTokens(context);
                    return Results.Text(tokens.RequestToken!);
                });
            application.MapControllerRoute(
                name: "default",
                pattern: "{controller}/{action}/{id?}");

            await application.StartAsync();

            var server = application.Services.GetRequiredService<IServer>();
            var address = server.Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer()
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(address)
            };

            return new BindingTestHost(application, client);
        }

        public Task<string> GetAntiForgeryTokenAsync()
        {
            return Client.GetStringAsync("/antiforgery-token");
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class RecordingPipelineService : IPipelineService
    {
        private readonly Dictionary<Guid, PipelineDefinition> _pipelines;

        public RecordingPipelineService(params PipelineDefinition[] pipelines)
        {
            _pipelines = pipelines.ToDictionary(pipeline => pipeline.Id);
        }

        public Guid? UpdatedId { get; private set; }

        public Guid? DeletedId { get; private set; }

        public bool Contains(Guid id)
        {
            return _pipelines.ContainsKey(id);
        }

        public PipelineDefinition GetRequired(Guid id)
        {
            return _pipelines[id];
        }

        public Task<PipelineDefinition> CreateAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<PipelineDefinition?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            _pipelines.TryGetValue(id, out var pipeline);
            return Task.FromResult(pipeline);
        }

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> UpdateAsync(
            Guid id,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            UpdatedId = id;

            if (!_pipelines.ContainsKey(id))
            {
                return Task.FromResult(false);
            }

            _pipelines[id] = pipeline;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            DeletedId = id;
            return Task.FromResult(_pipelines.Remove(id));
        }
    }
}
