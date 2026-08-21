using System.Net;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Transformations;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
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

    [Theory]
    [InlineData(TransformationType.SetDefaultValue)]
    [InlineData(TransformationType.FindAndReplace)]
    public async Task TransformationPost_OmittedRequiredValueDoesNotCallRuleService(
        TransformationType type)
    {
        var pipeline = CreatePipeline("Route pipeline");
        var pipelineService = new RecordingPipelineService(pipeline);
        var ruleService = new RecordingTransformationRuleService();
        await using var host = await BindingTestHost.StartAsync(pipelineService, ruleService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();
        var form = new Dictionary<string, string>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name",
            ["__RequestVerificationToken"] = antiForgeryToken
        };
        if (type == TransformationType.FindAndReplace) form["Find"] = "find";

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Transformations/Create",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(ruleService.LastInput);
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue, "DefaultValue")]
    [InlineData(TransformationType.FindAndReplace, "Replace")]
    public async Task TransformationPost_ExplicitEmptyValueRemainsEmpty(
        TransformationType type,
        string postedField)
    {
        var pipeline = CreatePipeline("Route pipeline");
        var pipelineService = new RecordingPipelineService(pipeline);
        var ruleService = new RecordingTransformationRuleService();
        await using var host = await BindingTestHost.StartAsync(pipelineService, ruleService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();
        var form = new Dictionary<string, string>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name",
            [postedField] = "",
            ["__RequestVerificationToken"] = antiForgeryToken
        };
        if (type == TransformationType.FindAndReplace) form["Find"] = "find";

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Transformations/Create",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var input = Assert.IsType<TransformationRuleInput>(ruleService.LastInput);
        Assert.Equal("", type == TransformationType.SetDefaultValue ? input.DefaultValue : input.Replace);
    }

    [Theory]
    [InlineData(TransformationType.SetDefaultValue, "DefaultValue")]
    [InlineData(TransformationType.FindAndReplace, "Find")]
    [InlineData(TransformationType.FindAndReplace, "Replace")]
    public async Task TransformationPost_WhitespaceValuesRemainUnchanged(
        TransformationType type,
        string postedField)
    {
        var pipeline = CreatePipeline("Route pipeline");
        var ruleService = new RecordingTransformationRuleService();
        await using var host = await BindingTestHost.StartAsync(new RecordingPipelineService(pipeline), ruleService);
        var antiForgeryToken = await host.GetAntiForgeryTokenAsync();
        var form = new Dictionary<string, string>
        {
            ["Type"] = type.ToString(),
            ["SourceField"] = "name",
            ["__RequestVerificationToken"] = antiForgeryToken
        };
        if (type == TransformationType.FindAndReplace)
        {
            form["Find"] = "find";
            form["Replace"] = "replace";
        }
        form[postedField] = "  ";

        using var response = await host.Client.PostAsync(
            $"/Pipelines/{pipeline.Id}/Transformations/Create",
            new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var input = Assert.IsType<TransformationRuleInput>(ruleService.LastInput);
        Assert.Equal("  ", postedField switch
        {
            "DefaultValue" => input.DefaultValue,
            "Find" => input.Find,
            _ => input.Replace
        });
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

        public static async Task<BindingTestHost> StartAsync(
            IPipelineService pipelineService,
            ITransformationRuleService? transformationRuleService = null)
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
            builder.Services.AddSingleton<ITransformationRuleService>(
                transformationRuleService ?? new RecordingTransformationRuleService());

            var application = builder.Build();
            application.MapGet(
                "/antiforgery-token",
                (HttpContext context, IAntiforgery antiForgery) =>
                {
                    var tokens = antiForgery.GetAndStoreTokens(context);
                    return Results.Text(tokens.RequestToken!);
                });
            application.MapControllers();
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

    private sealed class RecordingTransformationRuleService : ITransformationRuleService
    {
        public TransformationRuleInput? LastInput { get; private set; }

        public Task<TransformationRule?> CreateAsync(
            Guid pipelineId,
            TransformationRuleInput input,
            CancellationToken cancellationToken)
        {
            LastInput = input;
            return Task.FromResult<TransformationRule?>(new TransformationRule
            {
                Id = Guid.NewGuid(),
                Type = input.Type,
                SourceField = input.SourceField
            });
        }

        public Task<bool> UpdateAsync(Guid pipelineId, Guid ruleId, TransformationRuleInput input, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid pipelineId, Guid ruleId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
