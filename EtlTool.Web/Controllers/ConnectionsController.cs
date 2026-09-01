using EtlTool.Application.Connections;
using EtlTool.Domain.Enums;
using EtlTool.Web.Models.Connections;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

[Route("Connections")]
public sealed class ConnectionsController : Controller
{
    private readonly ISavedDatabaseConnectionService _service;

    public ConnectionsController(ISavedDatabaseConnectionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var connections = await _service.ListAsync(null, cancellationToken);
        return View(connections.Select(SavedConnectionListItemViewModel.From).ToList());
    }

    [HttpGet("Create")]
    public IActionResult Create(DatabaseProviderType? providerType = null)
    {
        var selected = providerType is DatabaseProviderType.MongoDb or DatabaseProviderType.PostgreSql
            ? providerType.Value
            : DatabaseProviderType.MongoDb;
        return View(new CreateSavedConnectionViewModel { ProviderType = selected });
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CreateSavedConnectionViewModel model,
        CancellationToken cancellationToken)
    {
        if (model.ProviderType is not DatabaseProviderType.MongoDb
            and not DatabaseProviderType.PostgreSql)
        {
            ModelState.AddModelError(nameof(model.ProviderType), "Choose MongoDB or PostgreSQL.");
        }

        if (ModelState.IsValid)
        {
            try
            {
                await _service.CreateAsync(
                    model.Name,
                    model.ProviderType,
                    model.ConnectionConfiguration,
                    cancellationToken);
                return RedirectToAction(nameof(Index));
            }
            catch (SavedConnectionConfigurationException exception)
            {
                ModelState.AddModelError(nameof(model.ConnectionConfiguration), exception.Message);
            }
            catch (ArgumentException exception) when (exception.ParamName == "name")
            {
                ModelState.AddModelError(nameof(model.Name), exception.Message);
            }
        }

        ClearSecretFromResponse(model);
        return View(model);
    }

    [HttpGet("{id:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return NotFound();
        var connection = await _service.GetByIdAsync(id, cancellationToken);
        if (connection is null) return NotFound();
        return View(new EditSavedConnectionViewModel
        {
            Id = connection.Id,
            Name = connection.Name,
            ProviderType = connection.ProviderType,
            ActiveRevision = connection.ActiveRevision
        });
    }

    [HttpPost("{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid id,
        EditSavedConnectionViewModel model,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || model.Id != id) return NotFound();
        var existing = await _service.GetByIdAsync(id, cancellationToken);
        if (existing is null) return NotFound();
        model.ProviderType = existing.ProviderType;
        model.ActiveRevision = existing.ActiveRevision;

        if (ModelState.IsValid)
        {
            try
            {
                var updated = await _service.UpdateAsync(
                    id,
                    model.Name,
                    model.ConnectionConfiguration,
                    cancellationToken);
                return updated is null
                    ? NotFound()
                    : RedirectToAction(nameof(Index));
            }
            catch (SavedConnectionConfigurationException exception)
            {
                ModelState.AddModelError(nameof(model.ConnectionConfiguration), exception.Message);
            }
            catch (ArgumentException exception) when (exception.ParamName == "name")
            {
                ModelState.AddModelError(nameof(model.Name), exception.Message);
            }
        }

        ClearSecretFromResponse(model);
        return View(model);
    }

    [HttpGet("{id:guid}/Delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return NotFound();
        var connection = await _service.GetByIdAsync(id, cancellationToken);
        return connection is null ? NotFound() : View(ToDeleteModel(connection));
    }

    [HttpPost("{id:guid}/Delete")]
    [ActionName(nameof(Delete))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return NotFound();
        var result = await _service.DeleteAsync(id, cancellationToken);
        if (result == SavedConnectionDeleteResult.NotFound) return NotFound();
        if (result == SavedConnectionDeleteResult.Referenced)
        {
            var connection = await _service.GetByIdAsync(id, cancellationToken);
            if (connection is null) return NotFound();
            Response.StatusCode = StatusCodes.Status409Conflict;
            return View(ToDeleteModel(
                connection,
                "This connection cannot be deleted while a pipeline or active run references it."));
        }

        return RedirectToAction(nameof(Index));
    }

    private void ClearSecretFromResponse(CreateSavedConnectionViewModel model)
    {
        PreserveErrorsAndRemoveAttemptedValue(nameof(model.ConnectionConfiguration));
        model.ConnectionConfiguration = string.Empty;
    }

    private void ClearSecretFromResponse(EditSavedConnectionViewModel model)
    {
        PreserveErrorsAndRemoveAttemptedValue(nameof(model.ConnectionConfiguration));
        model.ConnectionConfiguration = null;
    }

    private void PreserveErrorsAndRemoveAttemptedValue(string key)
    {
        var errors = ModelState[key]?.Errors
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray() ?? [];
        ModelState.Remove(key);
        foreach (var error in errors)
        {
            ModelState.AddModelError(key, error);
        }
    }

    private static DeleteSavedConnectionViewModel ToDeleteModel(
        EtlTool.Domain.Entities.SavedDatabaseConnection connection,
        string? failureMessage = null) => new()
    {
        Id = connection.Id,
        Name = connection.Name,
        ProviderDisplay = connection.ProviderType == DatabaseProviderType.PostgreSql
            ? "PostgreSQL"
            : "MongoDB",
        FailureMessage = failureMessage
    };
}
