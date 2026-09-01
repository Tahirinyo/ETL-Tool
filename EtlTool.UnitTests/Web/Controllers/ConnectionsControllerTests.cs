using EtlTool.Application.Connections;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class ConnectionsControllerTests
{
    [Fact]
    public async Task Create_InvalidConfiguration_DoesNotReturnSubmittedSecret()
    {
        const string distinctiveSecret = "mongodb://user:distinctive-password@localhost";
        var controller = new ConnectionsController(new StubService
        {
            CreateFailure = new SavedConnectionConfigurationException("The MongoDB connection configuration is malformed.")
        });
        var model = new CreateSavedConnectionViewModel
        {
            Name = "Local Mongo",
            ProviderType = DatabaseProviderType.MongoDb,
            ConnectionConfiguration = distinctiveSecret
        };

        var result = await controller.Create(model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var returned = Assert.IsType<CreateSavedConnectionViewModel>(view.Model);
        Assert.Equal(string.Empty, returned.ConnectionConfiguration);
        Assert.Null(controller.ModelState[nameof(model.ConnectionConfiguration)]?.AttemptedValue);
        Assert.DoesNotContain(
            controller.ModelState.SelectMany(entry => entry.Value?.Errors ?? []).Select(error => error.ErrorMessage),
            message => message.Contains("distinctive-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_BlankConfiguration_RetainsCurrentRevisionContract()
    {
        var id = Guid.NewGuid();
        var service = new StubService
        {
            Connection = new SavedDatabaseConnection
            {
                Id = id,
                Name = "Reporting",
                ProviderType = DatabaseProviderType.PostgreSql,
                ActiveRevision = 5
            }
        };
        var controller = new ConnectionsController(service);

        var result = await controller.Edit(id, new EditSavedConnectionViewModel
        {
            Id = id,
            Name = "Reporting renamed",
            ConnectionConfiguration = null
        }, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(service.LastReplacementConfiguration);
    }

    [Fact]
    public async Task Edit_InvalidModel_DoesNotReturnSubmittedReplacementSecret()
    {
        const string distinctiveSecret = "Host=localhost;Username=user;Password=distinctive-edit-secret";
        var id = Guid.NewGuid();
        var controller = new ConnectionsController(new StubService
        {
            Connection = new SavedDatabaseConnection
            {
                Id = id,
                Name = "Reporting",
                ProviderType = DatabaseProviderType.PostgreSql,
                ActiveRevision = 1
            }
        });
        controller.ModelState.AddModelError(nameof(EditSavedConnectionViewModel.Name), "Name is required.");

        var result = await controller.Edit(id, new EditSavedConnectionViewModel
        {
            Id = id,
            Name = string.Empty,
            ConnectionConfiguration = distinctiveSecret
        }, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var returned = Assert.IsType<EditSavedConnectionViewModel>(view.Model);
        Assert.Null(returned.ConnectionConfiguration);
        Assert.Null(controller.ModelState[nameof(returned.ConnectionConfiguration)]?.AttemptedValue);
        Assert.DoesNotContain(
            controller.ModelState.SelectMany(entry => entry.Value?.Errors ?? []).Select(error => error.ErrorMessage),
            message => message.Contains("distinctive-edit-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_ReferencedConnection_ReturnsConflictWithSafeMessage()
    {
        var id = Guid.NewGuid();
        var service = new StubService
        {
            Connection = new SavedDatabaseConnection
            {
                Id = id,
                Name = "Local Mongo",
                ProviderType = DatabaseProviderType.MongoDb,
                ActiveRevision = 1
            },
            DeleteResult = SavedConnectionDeleteResult.Referenced
        };
        var controller = new ConnectionsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.DeleteConfirmed(id, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<DeleteSavedConnectionViewModel>(view.Model);
        Assert.Equal(StatusCodes.Status409Conflict, controller.Response.StatusCode);
        Assert.Contains("cannot be deleted", model.FailureMessage, StringComparison.Ordinal);
    }

    private sealed class StubService : ISavedDatabaseConnectionService
    {
        public SavedDatabaseConnection? Connection { get; init; }
        public Exception? CreateFailure { get; init; }
        public SavedConnectionDeleteResult DeleteResult { get; init; } = SavedConnectionDeleteResult.Deleted;
        public string? LastReplacementConfiguration { get; private set; }

        public Task<SavedDatabaseConnection> CreateAsync(string name, DatabaseProviderType providerType, string connectionConfiguration, CancellationToken cancellationToken)
        {
            if (CreateFailure is not null) throw CreateFailure;
            return Task.FromResult(Connection ?? new SavedDatabaseConnection());
        }

        public Task<SavedDatabaseConnection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Connection?.Id == id ? Connection : null);

        public Task<IReadOnlyList<SavedDatabaseConnection>> ListAsync(DatabaseProviderType? providerType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SavedDatabaseConnection>>(Connection is null ? [] : [Connection]);

        public Task<SavedDatabaseConnection?> UpdateAsync(Guid id, string name, string? replacementConfiguration, CancellationToken cancellationToken)
        {
            LastReplacementConfiguration = replacementConfiguration;
            return Task.FromResult(Connection);
        }

        public Task<SavedConnectionDeleteResult> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteResult);
    }
}
