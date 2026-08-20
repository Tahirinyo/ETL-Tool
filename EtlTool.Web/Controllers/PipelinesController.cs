using EtlTool.Application.Pipelines;
using EtlTool.Application.Sources;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Web.Models.Pipelines;
using Microsoft.AspNetCore.Mvc;

namespace EtlTool.Web.Controllers;

public sealed class PipelinesController : Controller
{
    private readonly IPipelineService _pipelineService;
    private readonly ISourceInspectionService? _sourceInspectionService;

    public PipelinesController(IPipelineService pipelineService, ISourceInspectionService? sourceInspectionService = null)
    {
        ArgumentNullException.ThrowIfNull(pipelineService);
        _pipelineService = pipelineService;
        _sourceInspectionService = sourceInspectionService;
    }

    public async Task<IActionResult> Source(Guid id, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        return View(CreateSourceModel(pipeline));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InspectSource([FromRoute] Guid id, SourceUploadViewModel model, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        if (_sourceInspectionService is null) throw new InvalidOperationException("Source inspection is not configured.");
        if (model.SourceFile is null || model.SourceFile.Length == 0)
        {
            ModelState.AddModelError(nameof(model.SourceFile), "Choose a non-empty CSV or XLSX file.");
            return View("Source", model);
        }
        if (model.SourceType is not SourceType.Csv and not SourceType.Xlsx)
        {
            ModelState.AddModelError(nameof(model.SourceType), "Choose CSV or XLSX.");
            return View("Source", model);
        }
        if (model.SourceType == SourceType.Csv && !IsSupportedDelimiter(model.Delimiter))
        {
            ModelState.AddModelError(nameof(model.Delimiter), "Choose comma, semicolon, or tab as the CSV delimiter.");
            return View("Source", model);
        }
        await using var content = model.SourceFile.OpenReadStream();
        if (model.SourceType == SourceType.Xlsx)
        {
            var stage = await _sourceInspectionService.StageXlsxAsync(content, model.SourceFile.FileName, cancellationToken);
            return ApplyInspection(model, stage);
        }
        var options = CreateCsvOptions(pipeline, model.Delimiter);
        var inspection = await _sourceInspectionService.InspectCsvAsync(content, model.SourceFile.FileName, options, cancellationToken);
        var result = ApplyInspection(model, inspection);
        if (inspection.IsSuccess
            && !await SaveSourceAsync(id, pipeline, SourceType.Csv, options, cancellationToken))
        {
            return NotFound();
        }

        return result;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectWorksheet([FromRoute] Guid id, SourceUploadViewModel model, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);
        if (pipeline is null) return NotFound();
        if (_sourceInspectionService is null) throw new InvalidOperationException("Source inspection is not configured.");
        if (model.StageId is not Guid stageId || string.IsNullOrWhiteSpace(model.WorksheetName))
        {
            ModelState.AddModelError(nameof(model.WorksheetName), "Choose a worksheet.");
            return View("Source", model);
        }
        var inspection = await _sourceInspectionService.InspectStagedXlsxAsync(stageId, model.WorksheetName, cancellationToken);
        var result = ApplyInspection(model, inspection);
        if (inspection.IsSuccess)
        {
            var options = new SourceOptions { WorksheetName = model.WorksheetName, FirstRowIsHeader = true, CultureName = pipeline.SourceOptions.CultureName, DateFormat = pipeline.SourceOptions.DateFormat };
            if (!await SaveSourceAsync(id, pipeline, SourceType.Xlsx, options, cancellationToken))
            {
                return NotFound();
            }
        }

        return result;
    }

    private static SourceUploadViewModel CreateSourceModel(PipelineDefinition pipeline) => new()
    {
        SourceType = pipeline.SourceType is SourceType.Xlsx ? SourceType.Xlsx : SourceType.Csv,
        Delimiter = pipeline.SourceOptions.Delimiter ?? CsvDelimiter.Comma,
        WorksheetName = pipeline.SourceOptions.WorksheetName
    };

    private static SourceOptions CreateCsvOptions(PipelineDefinition pipeline, CsvDelimiter delimiter) => new()
    {
        Delimiter = delimiter, FirstRowIsHeader = true, CultureName = pipeline.SourceOptions.CultureName,
        DateFormat = pipeline.SourceOptions.DateFormat
    };

    private static bool IsSupportedDelimiter(CsvDelimiter delimiter) =>
        delimiter is CsvDelimiter.Comma or CsvDelimiter.Semicolon or CsvDelimiter.Tab;

    private async Task<bool> SaveSourceAsync(Guid id, PipelineDefinition pipeline, SourceType type, SourceOptions options, CancellationToken ct)
    {
        pipeline.SourceType = type;
        pipeline.SourceOptions = options;
        return await _pipelineService.UpdateAsync(id, pipeline, ct);
    }

    private IActionResult ApplyInspection(SourceUploadViewModel model, SourceInspectionResult inspection)
    {
        if (!inspection.IsSuccess) ModelState.AddModelError(string.Empty, inspection.ErrorMessage!);
        model.StageId = inspection.StageId;
        model.WorksheetNames = inspection.WorksheetNames;
        model.Columns = inspection.Columns;
        model.SampleRows = inspection.SampleRows.Select(row => new SourceSampleRowViewModel
        {
            SourceRowNumber = row.SourceRowNumber,
            Values = inspection.Columns.Select(column => Convert.ToString(row.Values.GetValueOrDefault(column), System.Globalization.CultureInfo.InvariantCulture)).ToList()
        }).ToList();
        return View("Source", model);
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var pipelines = await _pipelineService.ListAsync(cancellationToken);
        IReadOnlyList<PipelineListItemViewModel> model = pipelines
            .OrderByDescending(pipeline => pipeline.UpdatedAt)
            .Select(pipeline => new PipelineListItemViewModel
            {
                Id = pipeline.Id,
                Name = pipeline.Name,
                SourceType = pipeline.SourceType,
                DestinationDatabase = pipeline.DestinationDatabase,
                DestinationCollection = pipeline.DestinationCollection,
                UpdatedAt = pipeline.UpdatedAt
            })
            .ToList();

        return View(model);
    }

    public IActionResult Create()
    {
        return View(new PipelineFormViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        PipelineFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var pipeline = new PipelineDefinition
        {
            Name = model.Name,
            Description = model.Description
        };

        try
        {
            await _pipelineService.CreateAsync(pipeline, cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "pipeline")
        {
            ModelState.AddModelError(nameof(PipelineFormViewModel.Name), exception.Message);
            return View(model);
        }
        catch (DuplicatePipelineDefinitionException)
        {
            ModelState.AddModelError(
                string.Empty,
                "The pipeline could not be created because of an identity conflict. Please try again.");
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        ViewData["PipelineId"] = id;
        return View(new PipelineFormViewModel
        {
            Name = pipeline.Name,
            Description = pipeline.Description
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        [FromRoute] Guid id,
        PipelineFormViewModel model,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        ViewData["PipelineId"] = id;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        pipeline.Name = model.Name;
        pipeline.Description = model.Description;

        try
        {
            if (!await _pipelineService.UpdateAsync(id, pipeline, cancellationToken))
            {
                return NotFound();
            }
        }
        catch (ArgumentException exception) when (exception.ParamName == "pipeline")
        {
            ModelState.AddModelError(nameof(PipelineFormViewModel.Name), exception.Message);
            return View(model);
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var pipeline = await _pipelineService.GetByIdAsync(id, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        return View(new PipelineDeleteViewModel
        {
            Id = id,
            Name = pipeline.Name
        });
    }

    [HttpPost]
    [ActionName(nameof(Delete))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        if (!await _pipelineService.DeleteAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return RedirectToAction(nameof(Index));
    }
}
