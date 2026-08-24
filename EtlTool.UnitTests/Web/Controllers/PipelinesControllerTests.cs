using System.ComponentModel.DataAnnotations;
using System.Reflection;
using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Controllers;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace EtlTool.UnitTests.Web.Controllers;

public sealed class PipelinesControllerTests
{
    [Fact]
    public async Task Index_RequestsServiceAndMapsNewestPipelineFirst()
    {
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();
        IReadOnlyList<PipelineDefinition> pipelines =
        [
            new()
            {
                Id = olderId,
                Name = "Older",
                UpdatedAt = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero)
            },
            new()
            {
                Id = newerId,
                Name = "Newer",
                SourceType = SourceType.Csv,
                DestinationDatabase = "analytics",
                DestinationCollection = "customers",
                UpdatedAt = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero)
            }
        ];
        var service = new RecordingPipelineService
        {
            ListHandler = _ => Task.FromResult(pipelines)
        };
        var controller = new PipelinesController(service);
        using var cancellationSource = new CancellationTokenSource();

        var result = await controller.Index(cancellationSource.Token);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IReadOnlyList<PipelineListItemViewModel>>(view.Model);
        Assert.Equal([newerId, olderId], model.Select(item => item.Id));
        Assert.Equal(SourceType.Csv, model[0].SourceType);
        Assert.Equal("analytics", model[0].DestinationDatabase);
        Assert.Equal("customers", model[0].DestinationCollection);
        Assert.Equal("Not configured", model[1].SourceTypeDisplay);
        Assert.Equal("Not configured", model[1].DestinationDatabaseDisplay);
        Assert.Equal(cancellationSource.Token, service.ListCancellationToken);
        Assert.Equal(1, service.ListCallCount);
    }

    [Fact]
    public async Task Index_EmptyServiceResultReturnsEmptyViewModel()
    {
        var controller = new PipelinesController(new RecordingPipelineService());

        var result = await controller.Index(CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsAssignableFrom<IReadOnlyList<PipelineListItemViewModel>>(view.Model);
        Assert.Empty(model);
    }

    [Fact]
    public void Create_GetReturnsEmptyForm()
    {
        var controller = new PipelinesController(new RecordingPipelineService());

        var result = controller.Create();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PipelineFormViewModel>(view.Model);
        Assert.Equal(string.Empty, model.Name);
        Assert.Null(model.Description);
    }

    [Fact]
    public async Task Create_ValidPostMapsDraftAndRedirects()
    {
        var createdId = Guid.NewGuid();
        var service = new RecordingPipelineService
        {
            CreateHandler = (pipeline, _) =>
            {
                pipeline.Id = createdId;
                return Task.FromResult(pipeline);
            }
        };
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel
        {
            Name = "Customer import",
            Description = "Draft description"
        };
        using var cancellationSource = new CancellationTokenSource();

        var result = await controller.Create(model, cancellationSource.Token);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PipelinesController.Source), redirect.ActionName);
        Assert.Equal(createdId, Assert.IsType<Guid>(redirect.RouteValues!["id"]));
        var pipeline = Assert.IsType<PipelineDefinition>(service.CreatedPipeline);
        Assert.Equal(model.Name, pipeline.Name);
        Assert.Equal(model.Description, pipeline.Description);
        Assert.Equal(createdId, pipeline.Id);
        Assert.Equal(SourceType.Unspecified, pipeline.SourceType);
        Assert.Empty(pipeline.FieldMappings);
        Assert.Empty(pipeline.TransformationRules);
        Assert.Empty(pipeline.ValidationRules);
        Assert.Equal(cancellationSource.Token, service.CreateCancellationToken);
        Assert.Equal(1, service.CreateCallCount);
    }

    [Fact]
    public async Task Create_InvalidModelDoesNotCallService()
    {
        var service = new RecordingPipelineService();
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel { Name = string.Empty };
        controller.ModelState.AddModelError(nameof(model.Name), "Name is required.");

        var result = await controller.Create(model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Same(model, view.Model);
        Assert.Equal(0, service.CreateCallCount);
    }

    [Fact]
    public async Task Create_ServiceValidationFailureAddsNameError()
    {
        var service = new RecordingPipelineService
        {
            CreateHandler = (_, _) => Task.FromException<PipelineDefinition>(
                new ArgumentException("Pipeline name cannot be empty.", "pipeline"))
        };
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel { Name = "Submitted" };

        var result = await controller.Create(model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Same(model, view.Model);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[nameof(model.Name)]!.Errors,
            error => error.ErrorMessage.Contains("cannot be empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_DuplicateFailureAddsModelError()
    {
        var service = new RecordingPipelineService
        {
            CreateHandler = (_, _) => Task.FromException<PipelineDefinition>(
                new DuplicatePipelineDefinitionException(Guid.NewGuid()))
        };
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel { Name = "Submitted" };

        var result = await controller.Create(model, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(
            controller.ModelState[string.Empty]!.Errors,
            error => error.ErrorMessage.Contains("identity conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_GetMapsExistingPipelineAndHandlesInvalidOrMissingIds()
    {
        var id = Guid.NewGuid();
        var existing = new PipelineDefinition
        {
            Id = id,
            Name = "Existing",
            Description = "Description"
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (requestedId, _) => Task.FromResult<PipelineDefinition?>(
                requestedId == id ? existing : null)
        };
        var controller = new PipelinesController(service);

        var foundResult = await controller.Edit(id, CancellationToken.None);
        var missingResult = await controller.Edit(Guid.NewGuid(), CancellationToken.None);
        var emptyResult = await controller.Edit(Guid.Empty, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(foundResult);
        var model = Assert.IsType<PipelineFormViewModel>(view.Model);
        Assert.Equal(existing.Name, model.Name);
        Assert.Equal(existing.Description, model.Description);
        Assert.Equal(id, controller.ViewData["PipelineId"]);
        Assert.IsType<NotFoundResult>(missingResult);
        Assert.IsType<NotFoundResult>(emptyResult);
        Assert.Equal(2, service.GetByIdCallCount);
    }

    [Fact]
    public async Task Edit_ValidPostPreservesHiddenAggregateStateAndRedirects()
    {
        var id = Guid.NewGuid();
        var sourceOptions = new SourceOptions
        {
            CultureName = "tr-TR",
            Delimiter = CsvDelimiter.Semicolon
        };
        var mappings = new List<FieldMapping>
        {
            new() { SourceField = "customer_id", TargetField = "customerId" }
        };
        var transformations = new List<TransformationRule>
        {
            new() { Id = Guid.NewGuid(), Order = 1 }
        };
        var validations = new List<ValidationRule>
        {
            new() { Id = Guid.NewGuid(), Field = "customerId" }
        };
        var existing = new PipelineDefinition
        {
            Id = id,
            Name = "Before",
            Description = "Before description",
            SourceType = SourceType.Csv,
            SourceOptions = sourceOptions,
            FieldMappings = mappings,
            TransformationRules = transformations,
            ValidationRules = validations,
            DestinationDatabase = "analytics",
            DestinationCollection = "customers",
            UpsertKeyField = "customerId",
            CreatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(existing),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel
        {
            Name = "After",
            Description = "After description",
            DestinationDatabase = "warehouse",
            DestinationCollection = "curatedCustomers",
            UpsertKeyField = "customerId"
        };
        using var cancellationSource = new CancellationTokenSource();

        var result = await controller.Edit(id, model, cancellationSource.Token);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(PipelinesController.Index), redirect.ActionName);
        Assert.Same(existing, service.UpdatedPipeline);
        Assert.Equal(id, service.UpdatedId);
        Assert.Equal("After", existing.Name);
        Assert.Equal("After description", existing.Description);
        Assert.Same(sourceOptions, existing.SourceOptions);
        Assert.Same(mappings, existing.FieldMappings);
        Assert.Same(transformations, existing.TransformationRules);
        Assert.Same(validations, existing.ValidationRules);
        Assert.Equal("warehouse", existing.DestinationDatabase);
        Assert.Equal("curatedCustomers", existing.DestinationCollection);
        Assert.Equal("customerId", existing.UpsertKeyField);
        Assert.Equal(cancellationSource.Token, service.GetByIdCancellationTokens.Single());
        Assert.Equal(cancellationSource.Token, service.UpdateCancellationToken);
    }

    [Fact]
    public async Task Edit_InvalidPostReloadsAuthoritativeMappedFieldsWithoutUpdating()
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(new PipelineDefinition
            {
                Id = id,
                Name = "Existing",
                FieldMappings = [new FieldMapping { SourceField = "Id", TargetField = "id" }]
            })
        };
        var controller = new PipelinesController(service);
        var model = new PipelineFormViewModel { Name = string.Empty };
        controller.ModelState.AddModelError(nameof(model.Name), "Name is required.");

        var result = await controller.Edit(id, model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Same(model, view.Model);
        Assert.Equal(["id"], model.AvailableMappedFields);
        Assert.Equal(1, service.GetByIdCallCount);
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Edit_PostReturnsNotFoundForMissingRecordOrUpdateRace()
    {
        var id = Guid.NewGuid();
        var model = new PipelineFormViewModel { Name = "Updated" };
        var missingController = new PipelinesController(new RecordingPipelineService());
        var raceService = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(
                new PipelineDefinition { Id = id, Name = "Existing" }),
            UpdateHandler = (_, _, _) => Task.FromResult(false)
        };
        var raceController = new PipelinesController(raceService);

        var missingResult = await missingController.Edit(id, model, CancellationToken.None);
        var raceResult = await raceController.Edit(id, model, CancellationToken.None);

        Assert.IsType<NotFoundResult>(missingResult);
        Assert.IsType<NotFoundResult>(raceResult);
        Assert.Equal(1, raceService.UpdateCallCount);
    }

    [Fact]
    public async Task Delete_GetLoadsConfirmationAndHandlesMissingIds()
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (requestedId, _) => Task.FromResult<PipelineDefinition?>(
                requestedId == id
                    ? new PipelineDefinition { Id = id, Name = "Delete me" }
                    : null)
        };
        var controller = new PipelinesController(service);

        var foundResult = await controller.Delete(id, CancellationToken.None);
        var missingResult = await controller.Delete(Guid.NewGuid(), CancellationToken.None);
        var emptyResult = await controller.Delete(Guid.Empty, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(foundResult);
        var model = Assert.IsType<PipelineDeleteViewModel>(view.Model);
        Assert.Equal(id, model.Id);
        Assert.Equal("Delete me", model.Name);
        Assert.IsType<NotFoundResult>(missingResult);
        Assert.IsType<NotFoundResult>(emptyResult);
        Assert.Equal(2, service.GetByIdCallCount);
    }

    [Fact]
    public async Task Delete_PostCallsServiceAndHandlesMissingRecord()
    {
        var existingId = Guid.NewGuid();
        var service = new RecordingPipelineService
        {
            DeleteHandler = (id, _) => Task.FromResult(id == existingId)
        };
        var controller = new PipelinesController(service);
        using var cancellationSource = new CancellationTokenSource();

        var deletedResult = await controller.DeleteConfirmed(existingId, cancellationSource.Token);
        var missingResult = await controller.DeleteConfirmed(Guid.NewGuid(), cancellationSource.Token);
        var emptyResult = await controller.DeleteConfirmed(Guid.Empty, cancellationSource.Token);

        var redirect = Assert.IsType<RedirectToActionResult>(deletedResult);
        Assert.Equal(nameof(PipelinesController.Index), redirect.ActionName);
        Assert.IsType<NotFoundResult>(missingResult);
        Assert.IsType<NotFoundResult>(emptyResult);
        Assert.Equal(2, service.DeleteCallCount);
        Assert.All(
            service.DeleteCancellationTokens,
            token => Assert.Equal(cancellationSource.Token, token));
    }

    [Fact]
    public void PostActionsRequirePostAndAntiForgeryAndControllerDependsOnlyOnService()
    {
        var postActions = new[]
        {
            FindAction(nameof(PipelinesController.Create), 2),
            FindAction(nameof(PipelinesController.Edit), 3),
            FindAction(nameof(PipelinesController.DeleteConfirmed), 2)
        };

        Assert.All(postActions, action =>
        {
            Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        });

        var deleteActionName = postActions[2].GetCustomAttribute<ActionNameAttribute>();
        Assert.Equal(nameof(PipelinesController.Delete), deleteActionName?.Name);

        var constructor = Assert.Single(typeof(PipelinesController).GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Equal(typeof(IPipelineService), parameters[0].ParameterType);
        Assert.Equal(typeof(ISourceInspectionService), parameters[1].ParameterType);
    }

    [Theory]
    [InlineData(CsvDelimiter.Comma)]
    [InlineData(CsvDelimiter.Semicolon)]
    [InlineData(CsvDelimiter.Tab)]
    public async Task InspectSource_CsvForwardsSupportedDelimiterAndSurfacesColumnsAndRows(CsvDelimiter delimiter)
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition { Id = id, Name = "Import" };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };
        var inspection = new RecordingSourceInspectionService();
        var controller = new PipelinesController(service, inspection);
        var file = new FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Id;Name\n1;Ada")), 0, 13, "SourceFile", "customers.csv");

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv, Delimiter = delimiter, SourceFile = file
        }, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SourceUploadViewModel>(view.Model);
        Assert.Equal(delimiter, inspection.CsvOptions!.Delimiter);
        Assert.Equal(["Id", "Name"], model.Columns);
        Assert.Single(model.SampleRows);
        Assert.Equal(SourceType.Csv, pipeline.SourceType);
        Assert.Equal(
            [("Id", SourceFieldType.Integer), ("Name", SourceFieldType.String)],
            pipeline.ExpectedSchema.Select(field => (field.Name, field.DataType)));
    }

    [Fact]
    public async Task SelectWorksheet_ForwardsSelectedWorksheetAndPersistsOnlySuccessfulInspection()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Keep me",
            Description = "Unchanged",
            SourceOptions = new SourceOptions { CultureName = "tr-TR", DateFormat = "dd.MM.yyyy" }
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };
        var inspection = new RecordingSourceInspectionService();
        var controller = new PipelinesController(service, inspection);
        var stageId = Guid.NewGuid();

        var result = await controller.SelectWorksheet(id, new SourceUploadViewModel
        {
            StageId = stageId, WorksheetName = "Second"
        }, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SourceUploadViewModel>(view.Model);
        Assert.Equal(stageId, inspection.StageId);
        Assert.Equal(id, inspection.PipelineId);
        Assert.Equal("Second", inspection.WorksheetName);
        Assert.Equal("tr-TR", inspection.XlsxOptions!.CultureName);
        Assert.Equal("dd.MM.yyyy", inspection.XlsxOptions.DateFormat);
        Assert.Equal(SourceType.Xlsx, pipeline.SourceType);
        Assert.Equal("Second", pipeline.SourceOptions.WorksheetName);
        Assert.Equal("Keep me", pipeline.Name);
        Assert.Equal("Unchanged", pipeline.Description);
        Assert.Equal(["SecondId"], model.Columns);
        Assert.Equal(
            [("SecondId", SourceFieldType.Integer)],
            pipeline.ExpectedSchema.Select(field => (field.Name, field.DataType)));
    }

    [Fact]
    public async Task SelectWorksheet_CrossPipelineStageFailureDoesNotModifyTargetPipeline()
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var originalSchema = new SourceFieldDefinition { Name = "Existing", DataType = SourceFieldType.String };
        var owner = new PipelineDefinition { Id = ownerId, Name = "Owner" };
        var other = new PipelineDefinition
        {
            Id = otherId,
            Name = "Other",
            SourceType = SourceType.Csv,
            SourceOptions = new SourceOptions { Delimiter = CsvDelimiter.Semicolon },
            ExpectedSchema = [originalSchema]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (requestedId, _) => Task.FromResult<PipelineDefinition?>(
                requestedId == ownerId ? owner : requestedId == otherId ? other : null),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };
        var inspection = new RecordingSourceInspectionService
        {
            XlsxInspectionHandler = pipelineId => pipelineId == ownerId
                ? null
                : new SourceInspectionResult
                {
                    SourceType = SourceType.Xlsx,
                    ErrorMessage = "The uploaded workbook was staged for a different pipeline."
                }
        };
        var controller = new PipelinesController(service, inspection);
        var stageId = Guid.NewGuid();

        var rejected = await controller.SelectWorksheet(otherId, new SourceUploadViewModel
        {
            StageId = stageId,
            WorksheetName = "Data"
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(rejected);
        Assert.Equal(0, service.UpdateCallCount);
        Assert.Equal(SourceType.Csv, other.SourceType);
        Assert.Equal(CsvDelimiter.Semicolon, other.SourceOptions.Delimiter);
        Assert.Same(originalSchema, other.ExpectedSchema.Single());

        var selected = await controller.SelectWorksheet(ownerId, new SourceUploadViewModel
        {
            StageId = stageId,
            WorksheetName = "Data"
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(selected);
        Assert.Equal(1, service.UpdateCallCount);
        Assert.Equal(SourceType.Xlsx, owner.SourceType);
        Assert.Equal("Data", owner.SourceOptions.WorksheetName);
    }

    [Fact]
    public async Task InspectSource_CsvReturnsNotFoundWhenPipelineDisappearsDuringSave()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition { Id = id, Name = "Keep me", Description = "Unchanged" };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(false)
        };
        var inspection = new RecordingSourceInspectionService();
        var controller = new PipelinesController(service, inspection);
        var file = new FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Id,Name\n1,Ada")), 0, 13, "SourceFile", "customers.csv");

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv, Delimiter = CsvDelimiter.Comma, SourceFile = file
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(1, service.UpdateCallCount);
        Assert.Equal("Keep me", pipeline.Name);
        Assert.Equal("Unchanged", pipeline.Description);
    }

    [Fact]
    public async Task InspectSource_FailedInspectionDoesNotReplaceExpectedSchema()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Import",
            ExpectedSchema = [new SourceFieldDefinition { Name = "Original", DataType = SourceFieldType.String }]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var inspection = new RecordingSourceInspectionService { CsvFailure = true };
        var controller = new PipelinesController(service, inspection);
        var file = new FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Id,Name\n1,Ada")), 0, 13, "SourceFile", "customers.csv");

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        {
            SourceType = SourceType.Csv,
            Delimiter = CsvDelimiter.Comma,
            SourceFile = file
        }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(0, service.UpdateCallCount);
        Assert.Equal("Original", pipeline.ExpectedSchema.Single().Name);
        Assert.Equal(SourceFieldType.String, pipeline.ExpectedSchema.Single().DataType);
    }

    [Fact]
    public async Task SelectWorksheet_ReturnsNotFoundWhenPipelineDisappearsDuringSave()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition { Id = id, Name = "Keep me", Description = "Unchanged" };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(false)
        };
        var inspection = new RecordingSourceInspectionService();
        var controller = new PipelinesController(service, inspection);

        var result = await controller.SelectWorksheet(id, new SourceUploadViewModel
        {
            StageId = Guid.NewGuid(), WorksheetName = "Second"
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(1, service.UpdateCallCount);
        Assert.Equal("Keep me", pipeline.Name);
        Assert.Equal("Unchanged", pipeline.Description);
    }

    [Fact]
    public async Task InspectSource_MissingFileDoesNotInspectOrPersist()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition { Id = id, Name = "Import" };
        var service = new RecordingPipelineService { GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline) };
        var inspection = new RecordingSourceInspectionService();
        var controller = new PipelinesController(service, inspection);

        var result = await controller.InspectSource(id, new SourceUploadViewModel { SourceType = SourceType.Csv }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Null(inspection.CsvOptions);
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task InspectSource_UnsupportedDelimiterDoesNotInspectOrPersist()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition { Id = id, Name = "Import" };
        var service = new RecordingPipelineService { GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline) };
        var inspection = new RecordingSourceInspectionService();
        var controller = new PipelinesController(service, inspection);
        var file = new FormFile(new MemoryStream([1]), 0, 1, "SourceFile", "customers.csv");

        var result = await controller.InspectSource(id, new SourceUploadViewModel
        { SourceType = SourceType.Csv, Delimiter = (CsvDelimiter)999, SourceFile = file }, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Null(inspection.CsvOptions);
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Index_PreservesUnexpectedFailureAndCancellation()
    {
        var failure = new InvalidOperationException("Persistence failed.");
        var failingController = new PipelinesController(new RecordingPipelineService
        {
            ListHandler = _ => Task.FromException<IReadOnlyList<PipelineDefinition>>(failure)
        });
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancelledController = new PipelinesController(new RecordingPipelineService
        {
            ListHandler = token => Task.FromCanceled<IReadOnlyList<PipelineDefinition>>(token)
        });

        var actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingController.Index(CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledController.Index(cancellationSource.Token));

        Assert.Same(failure, actualFailure);
    }

    [Fact]
    public void PipelineFormViewModel_RejectsWhitespaceNameAndAllowsEmptyDescription()
    {
        var model = new PipelineFormViewModel
        {
            Name = "   ",
            Description = null
        };
        var validationResults = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            model,
            new ValidationContext(model),
            validationResults,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(
            validationResults,
            result => result.MemberNames.Contains(nameof(PipelineFormViewModel.Name)));
    }

    [Fact]
    public async Task Mapping_GetBuildsIncludedRowsFromInspectedSchema()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ],
            FieldMappings = []
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };

        var controller = new PipelinesController(service);

        var result = await controller.Mapping(id, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<FieldMappingViewModel>(view.Model);
        Assert.Collection(
            model.Fields,
            field =>
            {
                Assert.Equal("Id", field.SourceField);
                Assert.Equal("Id", field.TargetField);
                Assert.Equal(SourceFieldType.Integer, field.DataType);
                Assert.True(field.IsIncluded);
            },
            field =>
            {
                Assert.Equal("Email", field.SourceField);
                Assert.Equal("Email", field.TargetField);
                Assert.Equal(SourceFieldType.String, field.DataType);
                Assert.True(field.IsIncluded);
            });
        Assert.DoesNotContain(
            controller.ModelState[string.Empty]?.Errors ?? [],
            error => error.ErrorMessage.Contains("saved mapping configuration is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mapping_GetNullPersistedMappingsUsesAuthoritativeDefaultsAndConfigurationError()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ],
            FieldMappings = null!
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);

        var result = await controller.Mapping(id, CancellationToken.None);

        var model = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Collection(
            model.Fields,
            field =>
            {
                Assert.Equal("Id", field.SourceField);
                Assert.Equal("Id", field.TargetField);
                Assert.Equal(SourceFieldType.Integer, field.DataType);
                Assert.True(field.IsIncluded);
            },
            field =>
            {
                Assert.Equal("Email", field.SourceField);
                Assert.Equal("Email", field.TargetField);
                Assert.Equal(SourceFieldType.String, field.DataType);
                Assert.True(field.IsIncluded);
            });
        Assert.Null(pipeline.FieldMappings);
        Assert.Equal(0, service.UpdateCallCount);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains("saved mapping configuration is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mapping_GetRestoresValidPersistedMappingsUsingAuthoritativeSchema()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "Status", DataType = SourceFieldType.String }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "Id", TargetField = "Id", IsIncluded = true },
                new FieldMapping { SourceField = "Email", TargetField = "customerEmail", IsIncluded = true },
                new FieldMapping { SourceField = "Status", TargetField = string.Empty, IsIncluded = false }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };

        var result = await new PipelinesController(service).Mapping(id, CancellationToken.None);

        var model = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Collection(
            model.Fields,
            field =>
            {
                Assert.Equal("Id", field.SourceField);
                Assert.Equal("Id", field.TargetField);
                Assert.Equal(SourceFieldType.Integer, field.DataType);
                Assert.True(field.IsIncluded);
            },
            field =>
            {
                Assert.Equal("Email", field.SourceField);
                Assert.Equal("customerEmail", field.TargetField);
                Assert.Equal(SourceFieldType.String, field.DataType);
                Assert.True(field.IsIncluded);
            },
            field =>
            {
                Assert.Equal("Status", field.SourceField);
                Assert.Equal(string.Empty, field.TargetField);
                Assert.Equal(SourceFieldType.String, field.DataType);
                Assert.False(field.IsIncluded);
            });
    }

    [Fact]
    public async Task Mapping_GetInvalidPersistedMappingsUsesAuthoritativeDefaults()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema = [new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }],
            FieldMappings = [new FieldMapping { SourceField = "Unexpected", TargetField = "unexpected" }]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);

        var result = await controller.Mapping(id, CancellationToken.None);

        var model = Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("Id", model.Fields.Single().SourceField);
        Assert.Equal("Id", model.Fields.Single().TargetField);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains("saved mapping configuration is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mapping_GetWithoutInspectedSchemaReturnsConfigurationError()
    {
        var id = Guid.NewGuid();
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(new PipelineDefinition
            {
                Id = id,
                Name = "Customer import"
            })
        };
        var controller = new PipelinesController(service);

        var result = await controller.Mapping(id, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<FieldMappingViewModel>(view.Model);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains("Inspect a source schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mapping_GetUnknownPipelineReturnsNotFound()
    {
        var controller = new PipelinesController(new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(null)
        });

        var result = await controller.Mapping(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Mapping_PostValidConfigurationPersistsMappingsAndPreservesPipelineState()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String },
                new SourceFieldDefinition { Name = "Status", DataType = SourceFieldType.String }
            ],
            FieldMappings = [new FieldMapping { SourceField = "Existing", TargetField = "existing" }]
        };
        var sourceOptions = new SourceOptions { CultureName = "tr-TR", Delimiter = CsvDelimiter.Semicolon };
        pipeline.SourceOptions = sourceOptions;
        pipeline.DestinationDatabase = "analytics";
        pipeline.DestinationCollection = "customers";
        pipeline.UpsertKeyField = "customerId";
        pipeline.CreatedAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        pipeline.UpdatedAt = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        pipeline.TransformationRules = [new TransformationRule { Id = Guid.NewGuid(), Order = 1 }];
        pipeline.ValidationRules = [new ValidationRule { Id = Guid.NewGuid() }];
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };
        var model = new FieldMappingViewModel
        {
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "customerId", IsIncluded = true },
                new FieldMappingFieldViewModel { SourceField = "Email", TargetField = "Email", IsIncluded = true },
                new FieldMappingFieldViewModel { SourceField = "Status", TargetField = null, IsIncluded = false }
            ]
        };

        var result = await new PipelinesController(service).Mapping(id, model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var returned = Assert.IsType<FieldMappingViewModel>(view.Model);
        Assert.True(returned.IsSaved);
        Assert.Equal("customerId", returned.Fields[0].TargetField);
        Assert.True(returned.Fields[1].IsIncluded);
        Assert.Equal(SourceFieldType.Integer, returned.Fields[0].DataType);
        Assert.Equal(SourceFieldType.String, returned.Fields[1].DataType);
        Assert.Equal(SourceFieldType.String, returned.Fields[2].DataType);
        Assert.Equal(1, service.UpdateCallCount);
        Assert.Equal(id, service.UpdatedId);
        Assert.NotSame(pipeline, service.UpdatedPipeline);
        Assert.Same(sourceOptions, service.UpdatedPipeline!.SourceOptions);
        Assert.Same(pipeline.ExpectedSchema, service.UpdatedPipeline.ExpectedSchema);
        Assert.Same(pipeline.TransformationRules, service.UpdatedPipeline.TransformationRules);
        Assert.Same(pipeline.ValidationRules, service.UpdatedPipeline.ValidationRules);
        Assert.Equal("analytics", service.UpdatedPipeline.DestinationDatabase);
        Assert.Equal("customers", service.UpdatedPipeline.DestinationCollection);
        Assert.Equal("customerId", service.UpdatedPipeline.UpsertKeyField);
        Assert.Equal(pipeline.CreatedAt, service.UpdatedPipeline.CreatedAt);
        Assert.Equal(pipeline.UpdatedAt, service.UpdatedPipeline.UpdatedAt);
        Assert.Collection(service.UpdatedPipeline.FieldMappings,
            mapping =>
            {
                Assert.Equal("Id", mapping.SourceField);
                Assert.Equal("customerId", mapping.TargetField);
                Assert.True(mapping.IsIncluded);
            },
            mapping =>
            {
                Assert.Equal("Email", mapping.SourceField);
                Assert.Equal("Email", mapping.TargetField);
                Assert.True(mapping.IsIncluded);
            },
            mapping =>
            {
                Assert.Equal("Status", mapping.SourceField);
                Assert.Equal(string.Empty, mapping.TargetField);
                Assert.False(mapping.IsIncluded);
            });
        Assert.Collection(pipeline.FieldMappings, mapping =>
        {
            Assert.Equal("Existing", mapping.SourceField);
            Assert.Equal("existing", mapping.TargetField);
        });
    }

    [Fact]
    public async Task Mapping_PostRebuildsAuthoritativeRowsForTamperedSourceFieldsWithoutPersisting()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);
        var model = new FieldMappingViewModel
        {
            Fields = [new FieldMappingFieldViewModel { SourceField = "Unexpected", TargetField = "Id" }]
        };
        controller.ModelState.SetModelValue(
            "Fields[0].SourceField",
            new ValueProviderResult("Unexpected"));

        var result = await controller.Mapping(id, model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(model.IsSaved);
        var returned = Assert.IsType<FieldMappingViewModel>(view.Model);
        Assert.Collection(
            returned.Fields,
            field =>
            {
                Assert.Equal("Id", field.SourceField);
                Assert.Equal("Id", field.TargetField);
                Assert.Equal(SourceFieldType.Integer, field.DataType);
            },
            field =>
            {
                Assert.Equal("Email", field.SourceField);
                Assert.Equal("Email", field.TargetField);
                Assert.Equal(SourceFieldType.String, field.DataType);
            });
        Assert.DoesNotContain("Fields[0].SourceField", controller.ModelState.Keys);
        Assert.Equal("Id", returned.Fields[0].TargetField);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains("do not match", StringComparison.Ordinal));
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Mapping_PostRejectsMissingExpectedSourceField()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);

        var result = await controller.Mapping(
            id,
            new FieldMappingViewModel
            {
                Fields = [new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "id" }]
            },
            CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains("do not match", StringComparison.Ordinal));
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Mapping_PostRejectsDuplicateAndReorderedSourceFields()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);

        foreach (var fields in new[]
        {
            new List<FieldMappingFieldViewModel>
            {
                new() { SourceField = "Id", TargetField = "first" },
                new() { SourceField = "Id", TargetField = "second" }
            },
            new List<FieldMappingFieldViewModel>
            {
                new() { SourceField = "Email", TargetField = "email" },
                new() { SourceField = "Id", TargetField = "id" }
            }
        })
        {
            controller.ModelState.Clear();
            var result = await controller.Mapping(
                id,
                new FieldMappingViewModel { Fields = fields },
                CancellationToken.None);

            Assert.IsType<ViewResult>(result);
            Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
                error.ErrorMessage.Contains("do not match", StringComparison.Ordinal));
        }

        Assert.Equal(0, service.UpdateCallCount);
    }

    [Theory]
    [InlineData("empty-target")]
    [InlineData("all-excluded")]
    public async Task Mapping_PostSurfacesApplicableMappingServiceFailures(string caseName)
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema = [new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);
        var field = new FieldMappingFieldViewModel
        {
            SourceField = "Id",
            IsIncluded = caseName != "all-excluded",
            TargetField = caseName == "empty-target" ? "" : null
        };

        var result = await controller.Mapping(
            id,
            new FieldMappingViewModel { Fields = [field] },
            CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains(
                caseName == "empty-target" ? "empty target field" : "At least one active field mapping",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Mapping_PostAllowsExcludedFieldWithEmptyTarget()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };

        var result = await new PipelinesController(service).Mapping(
            id,
            new FieldMappingViewModel
            {
                Fields =
                [
                    new FieldMappingFieldViewModel { SourceField = "Id", IsIncluded = true, TargetField = "id" },
                    new FieldMappingFieldViewModel { SourceField = "Email", IsIncluded = false, TargetField = null }
                ]
            },
            CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.True(Assert.IsType<FieldMappingViewModel>(view.Model).IsSaved);
        Assert.Equal(1, service.UpdateCallCount);
    }

    [Fact]
    public async Task Mapping_PostSurfacesExistingMappingValidationErrors()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline)
        };
        var controller = new PipelinesController(service);
        var model = new FieldMappingViewModel
        {
            Fields =
            [
                new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "duplicate" },
                new FieldMappingFieldViewModel { SourceField = "Email", TargetField = "duplicate" }
            ]
        };

        var result = await controller.Mapping(id, model, CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.False(model.IsSaved);
        var returned = Assert.IsType<FieldMappingViewModel>(view.Model);
        Assert.Equal("duplicate", returned.Fields[0].TargetField);
        Assert.Equal("duplicate", returned.Fields[1].TargetField);
        Assert.True(returned.Fields[0].IsIncluded);
        Assert.True(returned.Fields[1].IsIncluded);
        Assert.Contains(controller.ModelState[string.Empty]!.Errors, error =>
            error.ErrorMessage.Contains("used by more than one active mapping", StringComparison.Ordinal));
        Assert.Equal(0, service.UpdateCallCount);
    }

    [Fact]
    public async Task Mapping_PostFailedUpdateReturnsNotFoundWithoutMutatingLoadedPipeline()
    {
        var id = Guid.NewGuid();
        var originalMappings = new List<FieldMapping>
        {
            new() { SourceField = "Id", TargetField = "original" }
        };
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema = [new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer }],
            FieldMappings = originalMappings
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(false)
        };

        var result = await new PipelinesController(service).Mapping(
            id,
            new FieldMappingViewModel
            {
                Fields = [new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "changed" }]
            },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Same(originalMappings, pipeline.FieldMappings);
        Assert.Equal("original", pipeline.FieldMappings.Single().TargetField);
        Assert.Equal(1, service.UpdateCallCount);
    }

    [Fact]
    public async Task Mapping_PostValidEditReplacesPreviousSavedMappings()
    {
        var id = Guid.NewGuid();
        var pipeline = new PipelineDefinition
        {
            Id = id,
            Name = "Customer import",
            ExpectedSchema =
            [
                new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                new SourceFieldDefinition { Name = "Email", DataType = SourceFieldType.String }
            ],
            FieldMappings =
            [
                new FieldMapping { SourceField = "Id", TargetField = "oldId" },
                new FieldMapping { SourceField = "Email", TargetField = "oldEmail" }
            ]
        };
        var service = new RecordingPipelineService
        {
            GetByIdHandler = (_, _) => Task.FromResult<PipelineDefinition?>(pipeline),
            UpdateHandler = (_, _, _) => Task.FromResult(true)
        };

        var result = await new PipelinesController(service).Mapping(
            id,
            new FieldMappingViewModel
            {
                Fields =
                [
                    new FieldMappingFieldViewModel { SourceField = "Id", TargetField = "newId" },
                    new FieldMappingFieldViewModel { SourceField = "Email", TargetField = string.Empty, IsIncluded = false }
                ]
            },
            CancellationToken.None);

        Assert.True(Assert.IsType<FieldMappingViewModel>(Assert.IsType<ViewResult>(result).Model).IsSaved);
        Assert.Collection(service.UpdatedPipeline!.FieldMappings,
            mapping =>
            {
                Assert.Equal("Id", mapping.SourceField);
                Assert.Equal("newId", mapping.TargetField);
                Assert.True(mapping.IsIncluded);
            },
            mapping =>
            {
                Assert.Equal("Email", mapping.SourceField);
                Assert.Equal(string.Empty, mapping.TargetField);
                Assert.False(mapping.IsIncluded);
            });
        Assert.DoesNotContain(service.UpdatedPipeline.FieldMappings, mapping =>
            mapping.TargetField is "oldId" or "oldEmail");
    }

    private static MethodInfo FindAction(string name, int parameterCount)
    {
        return typeof(PipelinesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);
    }

    private sealed class RecordingPipelineService : IPipelineService
    {
        public Func<PipelineDefinition, CancellationToken, Task<PipelineDefinition>>? CreateHandler { get; init; }

        public Func<Guid, CancellationToken, Task<PipelineDefinition?>>? GetByIdHandler { get; init; }

        public Func<CancellationToken, Task<IReadOnlyList<PipelineDefinition>>>? ListHandler { get; init; }

        public Func<Guid, PipelineDefinition, CancellationToken, Task<bool>>? UpdateHandler { get; init; }

        public Func<Guid, CancellationToken, Task<bool>>? DeleteHandler { get; init; }

        public int CreateCallCount { get; private set; }

        public PipelineDefinition? CreatedPipeline { get; private set; }

        public CancellationToken CreateCancellationToken { get; private set; }

        public int GetByIdCallCount => GetByIdIds.Count;

        public List<Guid> GetByIdIds { get; } = [];

        public List<CancellationToken> GetByIdCancellationTokens { get; } = [];

        public int ListCallCount { get; private set; }

        public CancellationToken ListCancellationToken { get; private set; }

        public int UpdateCallCount { get; private set; }

        public Guid UpdatedId { get; private set; }

        public PipelineDefinition? UpdatedPipeline { get; private set; }

        public CancellationToken UpdateCancellationToken { get; private set; }

        public int DeleteCallCount => DeleteIds.Count;

        public List<Guid> DeleteIds { get; } = [];

        public List<CancellationToken> DeleteCancellationTokens { get; } = [];

        public Task<PipelineDefinition> CreateAsync(
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;
            CreatedPipeline = pipeline;
            CreateCancellationToken = cancellationToken;

            return CreateHandler?.Invoke(pipeline, cancellationToken)
                ?? Task.FromResult(pipeline);
        }

        public Task<PipelineDefinition?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            GetByIdIds.Add(id);
            GetByIdCancellationTokens.Add(cancellationToken);

            return GetByIdHandler?.Invoke(id, cancellationToken)
                ?? Task.FromResult<PipelineDefinition?>(null);
        }

        public Task<IReadOnlyList<PipelineDefinition>> ListAsync(
            CancellationToken cancellationToken)
        {
            ListCallCount++;
            ListCancellationToken = cancellationToken;

            return ListHandler?.Invoke(cancellationToken)
                ?? Task.FromResult<IReadOnlyList<PipelineDefinition>>([]);
        }

        public Task<bool> UpdateAsync(
            Guid id,
            PipelineDefinition pipeline,
            CancellationToken cancellationToken)
        {
            UpdateCallCount++;
            UpdatedId = id;
            UpdatedPipeline = pipeline;
            UpdateCancellationToken = cancellationToken;

            return UpdateHandler?.Invoke(id, pipeline, cancellationToken)
                ?? Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            DeleteIds.Add(id);
            DeleteCancellationTokens.Add(cancellationToken);

            return DeleteHandler?.Invoke(id, cancellationToken)
                ?? Task.FromResult(false);
        }
    }

    private sealed class RecordingSourceInspectionService : ISourceInspectionService
    {
        public SourceOptions? CsvOptions { get; private set; }
        public bool CsvFailure { get; init; }
        public Guid? StageId { get; private set; }
        public Guid? PipelineId { get; private set; }
        public string? WorksheetName { get; private set; }
        public SourceOptions? XlsxOptions { get; private set; }
        public Func<Guid, SourceInspectionResult?>? XlsxInspectionHandler { get; init; }
        public Task<SourceInspectionResult> InspectCsvAsync(
            Guid pipelineId,
            Stream content,
            string fileName,
            SourceOptions options,
            CancellationToken cancellationToken)
        {
            PipelineId = pipelineId;
            CsvOptions = options;
            if (CsvFailure)
            {
                return Task.FromResult(new SourceInspectionResult
                {
                    SourceType = SourceType.Csv,
                    ErrorMessage = "Inspection failed."
                });
            }
            var row = new EtlTool.Application.Extraction.DataRow { SourceRowNumber = 2 };
            row.Values["Id"] = "1"; row.Values["Name"] = "Ada";
            return Task.FromResult(new SourceInspectionResult
            {
                SourceType = SourceType.Csv,
                Columns = ["Id", "Name"],
                SampleRows = [row],
                DetectedSchema =
                [
                    new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
                    new SourceFieldDefinition { Name = "Name", DataType = SourceFieldType.String }
                ],
                SourceReferenceId = Guid.NewGuid()
            });
        }
        public Task<SourceInspectionResult> StageXlsxAsync(
            Guid pipelineId,
            Stream content,
            string fileName,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SourceInspectionResult { SourceType = SourceType.Xlsx, StageId = Guid.NewGuid(), WorksheetNames = ["Data"] });
        public Task<SourceInspectionResult> InspectStagedXlsxAsync(
            Guid pipelineId,
            Guid stageId,
            string worksheetName,
            CancellationToken cancellationToken,
            SourceOptions? sourceOptions = null)
        {
            PipelineId = pipelineId;
            StageId = stageId;
            WorksheetName = worksheetName;
            XlsxOptions = sourceOptions;
            var handledResult = XlsxInspectionHandler?.Invoke(pipelineId);
            if (handledResult is not null) return Task.FromResult(handledResult);
            var row = new EtlTool.Application.Extraction.DataRow { SourceRowNumber = 2 };
            row.Values["SecondId"] = "2";
            return Task.FromResult(new SourceInspectionResult
            {
                SourceType = SourceType.Xlsx,
                Columns = ["SecondId"],
                SampleRows = [row],
                DetectedSchema = [new SourceFieldDefinition { Name = "SecondId", DataType = SourceFieldType.Integer }],
                SourceReferenceId = stageId
            });
        }
    }
}
