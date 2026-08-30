using System.Text;
using EtlTool.Application.Sources;
using EtlTool.Application.Uploads;
using EtlTool.Domain.Entities;
using EtlTool.Domain.Enums;
using EtlTool.Domain.ValueObjects;
using EtlTool.Infrastructure.Extraction;
using EtlTool.Infrastructure.Execution;
using EtlTool.Infrastructure.Sources;
using EtlTool.Infrastructure.Uploads;
using Microsoft.Extensions.Logging;
using static EtlTool.IntegrationTests.Extraction.OpenXmlWorkbookFixture;

namespace EtlTool.IntegrationTests.Sources;

public sealed class SourceInspectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"EtlTool-SourceInspection-{Guid.NewGuid():N}");
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task AcquireAsync_LogsStructuredSafeReasonsAndLifecycleTransitions()
    {
        var logger = new RecordingLogger<SourceInspectionService>();
        var service = CreateService(logger: logger);
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma, FirstRowIsHeader = true };

        Assert.Null(await service.AcquireAsync(
            Guid.NewGuid(), SourceType.Csv, options, CancellationToken.None));

        var pipelineId = Guid.NewGuid();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id\n1"));
        var inspection = await service.InspectCsvAsync(
            pipelineId, content, "customers.csv", options, CancellationToken.None);
        var sourceReferenceId = Assert.IsType<Guid>(inspection.SourceReferenceId);
        Assert.True(await service.ActivateAsync(pipelineId, sourceReferenceId, CancellationToken.None));

        await using (var acquired = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None)))
        {
            Assert.True(acquired.Content.CanRead);
        }

        Assert.Null(await service.AcquireAsync(
            pipelineId,
            SourceType.Csv,
            new SourceOptions { Delimiter = CsvDelimiter.Semicolon, FirstRowIsHeader = true },
            CancellationToken.None));
        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Xlsx, options, CancellationToken.None));

        await using (var reservation = Assert.IsAssignableFrom<IWizardRunSourceReservation>(
            await service.ReserveForRunAsync(pipelineId, SourceType.Csv, options, CancellationToken.None)))
        {
            Assert.Null(await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));
        }

        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));

        var missingPipelineId = Guid.NewGuid();
        var existingPaths = Directory.EnumerateFiles(_root, "*.upload").ToHashSet(StringComparer.Ordinal);
        await using var missingContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\n2"));
        var missingInspection = await service.InspectCsvAsync(
            missingPipelineId, missingContent, "missing.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            missingPipelineId,
            Assert.IsType<Guid>(missingInspection.SourceReferenceId),
            CancellationToken.None));
        var missingPath = Assert.Single(
            Directory.EnumerateFiles(_root, "*.upload"),
            path => !existingPaths.Contains(path));
        File.Delete(missingPath);
        Assert.Null(await service.AcquireAsync(
            missingPipelineId, SourceType.Csv, options, CancellationToken.None));

        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionFailureReason", "NoActiveSource"));
        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionFailureReason", "SourceOptionsMismatch"));
        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionFailureReason", "SourceTypeMismatch"));
        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionFailureReason", "RunReserved"));
        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionFailureReason", "Expired"));
        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionFailureReason", "PhysicalSourceUnavailable")
            && entry.Has("FileExists", false));
        Assert.Contains(logger.Entries, entry => entry.Has("AcquisitionResult", "Acquired"));
        Assert.Contains(logger.Entries, entry => entry.Has("SourceLifecycleTransition", "PendingCreated"));
        Assert.Contains(logger.Entries, entry => entry.Has("SourceLifecycleTransition", "Activated"));
        Assert.Contains(logger.Entries, entry => entry.Has("SourceLifecycleTransition", "ReservedForRun"));
        Assert.Contains(logger.Entries, entry => entry.Has("SourceLifecycleTransition", "RunReservationReleased"));
        Assert.Contains(logger.Entries, entry => entry.Has("SourceLifecycleTransition", "Expired"));
        Assert.Contains(logger.Entries, entry => entry.Has("SourceLifecycleTransition", "Retired")
            && entry.Has("SourceLifecycleState", "PhysicalSourceUnavailable"));
    }

    [Fact]
    public async Task InspectCsvAsync_UsesDelimiterBoundsSampleAndCleansStoredFile()
    {
        var content = "Id;Name\n" + string.Join("\n", Enumerable.Range(1, 101).Select(i => i == 101 ? $"later;Name{i}" : $"{i};Name{i}"));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var service = CreateService();

        var result = await service.InspectCsvAsync(stream, "customers.csv", new SourceOptions { Delimiter = CsvDelimiter.Semicolon }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Id", "Name"], result.Columns);
        Assert.Equal(100, result.SampleRows.Count);
        Assert.Equal("Name1", result.SampleRows[0].Values["Name"]);
        Assert.Equal(
            [SourceFieldType.Integer, SourceFieldType.String],
            result.DetectedSchema.Select(field => field.DataType));
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task InspectCsvAsync_UndefinedDelimiterReturnsControlledFailureWithoutStoring()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Id\n1"));
        var service = CreateService();

        var result = await service.InspectCsvAsync(
            stream, "customers.csv", new SourceOptions { Delimiter = (CsvDelimiter)999 }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("comma", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task StagedXlsx_SelectsRequestedWorksheetAndCleansStoredFile()
    {
        await using var stream = Create(
            Sheet("First", Row(Text(1, "FirstId")), Row(Number(1, 1))),
            Sheet("Second", Row(Text(1, "SecondId")), Row(Number(1, 2))));
        var service = CreateService();

        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);
        Assert.True(stage.IsSuccess);
        Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));

        var result = await service.InspectStagedXlsxAsync(stage.StageId!.Value, "Second", CancellationToken.None);

        Assert.Equal(["First", "Second"], stage.WorksheetNames);
        Assert.True(result.IsSuccess);
        Assert.Equal(["SecondId"], result.Columns);
        Assert.Equal(2d, result.SampleRows.Single().Values["SecondId"]);
        Assert.Equal(SourceFieldType.Integer, result.DetectedSchema.Single().DataType);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task StagedXlsx_CrossPipelineSelectionIsRejectedWithoutConsumingOwnerStage()
    {
        var ownerPipelineId = Guid.NewGuid();
        var otherPipelineId = Guid.NewGuid();
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var service = CreateService();

        var stage = await service.StageXlsxAsync(
            ownerPipelineId,
            stream,
            "source.xlsx",
            CancellationToken.None);
        var storedPath = Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));

        var rejected = await service.InspectStagedXlsxAsync(
            otherPipelineId,
            stage.StageId!.Value,
            "Data",
            CancellationToken.None);

        Assert.False(rejected.IsSuccess);
        Assert.Contains("different pipeline", rejected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(storedPath));

        var selected = await service.InspectStagedXlsxAsync(
            ownerPipelineId,
            stage.StageId.Value,
            "Data",
            CancellationToken.None);

        Assert.True(selected.IsSuccess);
        Assert.Equal(["Id"], selected.Columns);
        await service.DiscardAsync(
            Assert.IsType<Guid>(selected.SourceReferenceId),
            CancellationToken.None);
        Assert.False(File.Exists(storedPath));
    }

    [Fact]
    public async Task StagedXlsx_MissingExpiredAndConsumedStagesReturnControlledFailure()
    {
        var pipelineId = Guid.NewGuid();
        var service = CreateService();

        var missing = await service.InspectStagedXlsxAsync(
            pipelineId,
            Guid.NewGuid(),
            "Data",
            CancellationToken.None);

        Assert.False(missing.IsSuccess);
        Assert.Contains("expired", missing.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var expiredStream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var expiredStage = await service.StageXlsxAsync(pipelineId, expiredStream, "expired.xlsx", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(16));

        var expired = await service.InspectStagedXlsxAsync(
            pipelineId,
            expiredStage.StageId!.Value,
            "Data",
            CancellationToken.None);

        Assert.False(expired.IsSuccess);
        Assert.Contains("expired", expired.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));

        await using var consumedStream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var consumedStage = await service.StageXlsxAsync(pipelineId, consumedStream, "consumed.xlsx", CancellationToken.None);
        var consumed = await service.InspectStagedXlsxAsync(
            pipelineId,
            consumedStage.StageId!.Value,
            "Data",
            CancellationToken.None);
        var repeated = await service.InspectStagedXlsxAsync(
            pipelineId,
            consumedStage.StageId.Value,
            "Data",
            CancellationToken.None);

        Assert.True(consumed.IsSuccess);
        Assert.False(repeated.IsSuccess);
        Assert.Contains("expired", repeated.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await service.DiscardAsync(
            Assert.IsType<Guid>(consumed.SourceReferenceId),
            CancellationToken.None);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task CsvAndXlsxInspection_ReturnCompatibleSchemaSuggestions()
    {
        await using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes("Id,Amount,Name\n1,1.25,Ada"));
        await using var xlsxStream = Create(
            Sheet(
                "Data",
                Row(Text(1, "Id"), Text(2, "Amount"), Text(3, "Name")),
                Row(Number(1, 1), Number(2, 1.25), Text(3, "Ada"))));
        var service = CreateService();

        var csv = await service.InspectCsvAsync(
            csvStream,
            "source.csv",
            new SourceOptions { CultureName = "en-US", Delimiter = CsvDelimiter.Comma },
            CancellationToken.None);
        var stage = await service.StageXlsxAsync(xlsxStream, "source.xlsx", CancellationToken.None);
        var xlsx = await service.InspectStagedXlsxAsync(stage.StageId!.Value, "Data", CancellationToken.None);

        Assert.Equal(
            csv.DetectedSchema.Select(field => (field.Name, field.DataType)),
            xlsx.DetectedSchema.Select(field => (field.Name, field.DataType)));
    }

    [Fact]
    public async Task PendingWorksheetDiscovery_IsProtectedFromOrphanCleanupAndBecomesSelectable()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var blockingStorage = new BlockingAfterStoreStorage(localStorage);
        var service = CreateService(blockingStorage);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var stagingTask = service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);

        var upload = await blockingStorage.Stored.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.SetLastWriteTimeUtc(upload.StoredFilePath, _clock.GetUtcNow().AddMinutes(-16).UtcDateTime);

        try
        {
            await service.PurgeExpiredAsync();
            await service.PurgeOrphanedUploadsAsync(CancellationToken.None);
            Assert.True(File.Exists(upload.StoredFilePath));
        }
        finally
        {
            blockingStorage.Continue.TrySetResult();
        }

        var stage = await stagingTask;
        Assert.True(stage.IsSuccess);

        var selected = await service.InspectStagedXlsxAsync(
            stage.StageId!.Value,
            "Data",
            CancellationToken.None);

        Assert.True(selected.IsSuccess);
        Assert.False(File.Exists(upload.StoredFilePath));
    }

    [Fact]
    public async Task PendingWorksheetDiscovery_CancellationDeletesUploadAndPropagatesCancellation()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        using var cancellationSource = new CancellationTokenSource();
        var storage = new CancelAfterStoreStorage(localStorage, cancellationSource);
        var service = CreateService(storage);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StageXlsxAsync(stream, "source.xlsx", cancellationSource.Token));

        Assert.NotNull(storage.StoredUpload);
        Assert.False(File.Exists(storage.StoredUpload.StoredFilePath));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task PendingWorksheetDiscovery_CancellationRemainsAuthoritativeWhenDeletionFails()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var failingDeletion = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        using var cancellationSource = new CancellationTokenSource();
        var storage = new CancelAfterStoreStorage(failingDeletion, cancellationSource);
        var service = CreateService(storage);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.StageXlsxAsync(stream, "source.xlsx", cancellationSource.Token));

        Assert.Contains(exception.Data.Values.Cast<object>(), value => value is IOException);
        Assert.NotNull(failingDeletion.LastFailedUploadPath);
        Assert.True(File.Exists(failingDeletion.LastFailedUploadPath));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(failingDeletion.LastFailedUploadPath));
    }

    [Fact]
    public async Task PendingWorksheetDiscovery_MalformedWorkbookDeletesOwnedUploadAndReturnsFailure()
    {
        var service = CreateService();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not-an-xlsx-workbook"));

        var result = await service.StageXlsxAsync(
            stream,
            "source.xlsx",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("malformed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task PendingWorksheetDiscovery_MalformedFailureRemainsAuthoritativeWhenDeletionFails()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        var service = CreateService(storage);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not-an-xlsx-workbook"));

        var result = await service.StageXlsxAsync(
            stream,
            "source.xlsx",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("malformed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(storage.LastFailedUploadPath);
        Assert.True(File.Exists(storage.LastFailedUploadPath));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(storage.LastFailedUploadPath));
    }

    [Fact]
    public async Task PendingWorksheetDiscovery_SourceExceptionRemainsAuthoritativeWhenDeletionFails()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new MissingBeforeDiscoveryThenFailDeleteStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16));
        var service = CreateService(storage);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None));

        Assert.Contains(exception.Data.Values.Cast<object>(), value => value is IOException);
        Assert.NotNull(storage.FailedUploadPath);
        Assert.True(File.Exists(storage.FailedUploadPath));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(storage.FailedUploadPath));
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesExpiredRegisteredStage()
    {
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var service = CreateService();

        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(16));
        await service.PurgeExpiredAsync();

        Assert.True(stage.IsSuccess);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task PurgeExpiredAsync_FirstDeletionFailureDoesNotStrandLaterExpiredStages()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        var service = CreateService(storage);
        await using var firstStream = Create(Sheet("First", Row(Text(1, "Id")), Row(Number(1, 1))));
        await using var secondStream = Create(Sheet("Second", Row(Text(1, "Id")), Row(Number(1, 2))));
        var first = await service.StageXlsxAsync(firstStream, "first.xlsx", CancellationToken.None);
        var second = await service.StageXlsxAsync(secondStream, "second.xlsx", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(16));

        await Assert.ThrowsAsync<IOException>(service.PurgeExpiredAsync);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, storage.DeleteCallCount);
        Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);
        await service.PurgeExpiredAsync();

        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task PurgeOrphanedUploadsAsync_RemovesExpiredGeneratedUploadAfterRestartAndPreservesUnrelatedFiles()
    {
        var storage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        await using var content = new MemoryStream([1, 2, 3]);
        var orphan = await storage.StoreAsync(content, "source.xlsx", CancellationToken.None);
        var unrelatedInsideRoot = Path.Combine(_root, "keep.txt");
        var unrelatedOutsideRoot = $"{_root}-outside.upload";
        await File.WriteAllTextAsync(unrelatedInsideRoot, "keep");
        await File.WriteAllTextAsync(unrelatedOutsideRoot, "keep");
        File.SetLastWriteTimeUtc(orphan.StoredFilePath, _clock.GetUtcNow().AddMinutes(-16).UtcDateTime);
        var restartedStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var restartedService = CreateService(restartedStorage);

        try
        {
            await restartedService.PurgeOrphanedUploadsAsync(CancellationToken.None);

            Assert.False(File.Exists(orphan.StoredFilePath));
            Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedInsideRoot));
            Assert.Equal("keep", await File.ReadAllTextAsync(unrelatedOutsideRoot));
        }
        finally
        {
            File.Delete(unrelatedOutsideRoot);
        }
    }

    [Fact]
    public async Task PeriodicCleanup_RemovesRestartOrphanAfterItExpires()
    {
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var serviceBeforeRestart = CreateService();
        var stage = await serviceBeforeRestart.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);
        var storedPath = Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));
        File.SetLastWriteTimeUtc(storedPath, _clock.GetUtcNow().UtcDateTime);
        var serviceAfterRestart = CreateService();

        await serviceAfterRestart.PurgeOrphanedUploadsAsync(CancellationToken.None);
        Assert.True(File.Exists(storedPath));

        _clock.Advance(TimeSpan.FromMinutes(16));
        await serviceAfterRestart.PurgeExpiredAsync();
        await serviceAfterRestart.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.True(stage.IsSuccess);
        Assert.False(File.Exists(storedPath));
    }

    [Fact]
    public async Task PeriodicCleanup_PreservesRegisteredStageWhoseFileTimestampPredatesItsExpiry()
    {
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var service = CreateService();
        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);
        var storedPath = Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));
        File.SetLastWriteTimeUtc(storedPath, _clock.GetUtcNow().AddMinutes(-16).UtcDateTime);

        await service.PurgeExpiredAsync();
        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.True(stage.IsSuccess);
        Assert.True(File.Exists(storedPath));

        var selected = await service.InspectStagedXlsxAsync(stage.StageId!.Value, "Data", CancellationToken.None);
        Assert.True(selected.IsSuccess);
        Assert.False(File.Exists(storedPath));
    }

    [Fact]
    public async Task PeriodicCleanup_PreservesUploadWhileStagedSelectionIsActivelyInspecting()
    {
        var storage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var csv = new CsvFileExtractor();
        var xlsx = new XlsxFileExtractor();
        IUploadValidationService validation = new UploadValidationService(
            storage,
            new UploadValidationOptions { MaxFileSizeBytes = 1024 * 1024, MaxDataRowCount = 100_000 },
            csv,
            xlsx);
        var blockingValidation = new BlockingStoredValidationService(validation);
        var service = new SourceInspectionService(blockingValidation, storage, csv, xlsx, _clock);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);
        var storedPath = Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));

        var inspectionTask = service.InspectStagedXlsxAsync(
            stage.StageId!.Value,
            "Data",
            CancellationToken.None);
        await blockingValidation.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromMinutes(16));
        File.SetLastWriteTimeUtc(storedPath, _clock.GetUtcNow().AddMinutes(-16).UtcDateTime);

        try
        {
            await service.PurgeExpiredAsync();
            await service.PurgeOrphanedUploadsAsync(CancellationToken.None);
            Assert.True(File.Exists(storedPath));
        }
        finally
        {
            blockingValidation.Continue.TrySetResult();
        }

        var result = await inspectionTask;
        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(storedPath));
    }

    [Fact]
    public async Task ActiveInspectionFailure_RemainsAuthoritativeWhenDeletionFailsAndOrphanIsRecoverable()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        var csv = new CsvFileExtractor();
        var xlsx = new XlsxFileExtractor();
        IUploadValidationService validation = new UploadValidationService(
            storage,
            new UploadValidationOptions { MaxFileSizeBytes = 1024 * 1024, MaxDataRowCount = 100_000 },
            csv,
            xlsx);
        var failingValidation = new FailingStoredValidationService(validation);
        var service = new SourceInspectionService(failingValidation, storage, csv, xlsx, _clock);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => service.InspectStagedXlsxAsync(
                stage.StageId!.Value,
                "Data",
                CancellationToken.None));

        Assert.Same(failingValidation.InspectionFailure, exception);
        Assert.Contains(exception.Data.Values.Cast<object>(), value => value is IOException);
        Assert.NotNull(storage.LastFailedUploadPath);
        Assert.True(File.Exists(storage.LastFailedUploadPath));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(storage.LastFailedUploadPath));
    }

    [Fact]
    public async Task ActiveInspectionValidationFailure_RemainsControlledWhenDeletionFails()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        var service = CreateService(storage);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);

        var result = await service.InspectStagedXlsxAsync(
            stage.StageId!.Value,
            "Missing",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("malformed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(storage.LastFailedUploadPath);
        Assert.True(File.Exists(storage.LastFailedUploadPath));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(storage.LastFailedUploadPath));
    }

    [Fact]
    public async Task SuccessfulInspection_StillSurfacesDeletionFailureAndLeavesRecoverableOrphan()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        var service = CreateService(storage);
        await using var stream = Create(Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 1))));
        var stage = await service.StageXlsxAsync(stream, "source.xlsx", CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(
            () => service.InspectStagedXlsxAsync(
                stage.StageId!.Value,
                "Data",
                CancellationToken.None));

        Assert.NotNull(storage.LastFailedUploadPath);
        Assert.True(File.Exists(storage.LastFailedUploadPath));

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(storage.LastFailedUploadPath));
    }

    private SourceInspectionService CreateService(
        IUploadStorage? storage = null,
        IUploadValidationService? validation = null,
        ILogger<SourceInspectionService>? logger = null)
    {
        storage ??= new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var csv = new CsvFileExtractor();
        var xlsx = new XlsxFileExtractor();
        validation ??= new UploadValidationService(storage, new UploadValidationOptions
        {
            MaxFileSizeBytes = 1024 * 1024, MaxDataRowCount = 100_000
        }, csv, xlsx);
        return new SourceInspectionService(validation, storage, csv, xlsx, _clock, logger: logger);
    }

    [Fact]
    public async Task RetainedCsv_ActivatesOnlyForOwningPipelineAndMatchingOptions()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions
        {
            CultureName = "en-US",
            Delimiter = CsvDelimiter.Semicolon,
            FirstRowIsHeader = true
        };
        var service = CreateService();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id;Name\n1;Ada"));

        var inspection = await service.InspectCsvAsync(
            pipelineId,
            content,
            "customers.csv",
            options,
            CancellationToken.None);

        var reference = Assert.IsType<Guid>(inspection.SourceReferenceId);
        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Csv, options, CancellationToken.None));
        Assert.True(await service.ActivateAsync(pipelineId, reference, CancellationToken.None));
        Assert.Null(await service.AcquireAsync(
            Guid.NewGuid(), SourceType.Csv, options, CancellationToken.None));
        Assert.Null(await service.AcquireAsync(
            pipelineId,
            SourceType.Csv,
            new SourceOptions { CultureName = "en-US", Delimiter = CsvDelimiter.Comma },
            CancellationToken.None));

        await using var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));
        using var reader = new StreamReader(lease.Content, leaveOpen: true);
        Assert.Equal("Id;Name", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task RetainedSources_KeepDistinctActiveContentForEachPipeline()
    {
        var firstPipelineId = Guid.NewGuid();
        var secondPipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var firstContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nfirst"));
        await using var secondContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nsecond"));

        var first = await service.InspectCsvAsync(
            firstPipelineId, firstContent, "same-name.csv", options, CancellationToken.None);
        var second = await service.InspectCsvAsync(
            secondPipelineId, secondContent, "same-name.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            firstPipelineId, Assert.IsType<Guid>(first.SourceReferenceId), CancellationToken.None));
        Assert.True(await service.ActivateAsync(
            secondPipelineId, Assert.IsType<Guid>(second.SourceReferenceId), CancellationToken.None));

        await using var firstLease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(firstPipelineId, SourceType.Csv, options, CancellationToken.None));
        await using var secondLease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(secondPipelineId, SourceType.Csv, options, CancellationToken.None));
        using var firstReader = new StreamReader(firstLease.Content, leaveOpen: true);
        using var secondReader = new StreamReader(secondLease.Content, leaveOpen: true);

        _ = await firstReader.ReadLineAsync();
        _ = await secondReader.ReadLineAsync();
        Assert.Equal("first", await firstReader.ReadLineAsync());
        Assert.Equal("second", await secondReader.ReadLineAsync());
    }

    [Fact]
    public async Task ConcurrentSourceCommits_KeepFinalPersistedStatePairedWithItsRetainedSource()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions
        {
            CultureName = "en-US",
            Delimiter = CsvDelimiter.Comma,
            FirstRowIsHeader = true
        };
        var service = CreateService();
        var coordinator = new PipelineSourceCommitCoordinator(service);
        await using var firstContent = new MemoryStream(Encoding.UTF8.GetBytes("Id,Value\n1,first"));
        await using var secondContent = new MemoryStream(Encoding.UTF8.GetBytes("Id,Value\n1,second"));
        var firstInspection = await service.InspectCsvAsync(
            pipelineId, firstContent, "same.csv", options, CancellationToken.None);
        var secondInspection = await service.InspectCsvAsync(
            pipelineId, secondContent, "same.csv", options, CancellationToken.None);
        var firstReferenceId = Assert.IsType<Guid>(firstInspection.SourceReferenceId);
        var secondReferenceId = Assert.IsType<Guid>(secondInspection.SourceReferenceId);
        var firstPersistenceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPersistence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CommittedSourceState? persistedState = null;

        var firstCommit = coordinator.CommitAsync(
            pipelineId,
            firstReferenceId,
            async cancellationToken =>
            {
                persistedState = new CommittedSourceState(Pipeline(pipelineId, options), "first");
                firstPersistenceEntered.TrySetResult();
                await releaseFirstPersistence.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);
        await firstPersistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondCommit = coordinator.CommitAsync(
            pipelineId,
            secondReferenceId,
            _ =>
            {
                persistedState = new CommittedSourceState(Pipeline(pipelineId, options), "second");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        releaseFirstPersistence.TrySetResult();
        Assert.Equal(
            [PipelineSourceCommitStatus.Succeeded, PipelineSourceCommitStatus.Succeeded],
            await Task.WhenAll(firstCommit, secondCommit));

        var finalState = Assert.IsType<CommittedSourceState>(persistedState);
        await using var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(
                finalState.Pipeline.Id,
                finalState.Pipeline.SourceType,
                finalState.Pipeline.SourceOptions,
                CancellationToken.None));
        using var reader = new StreamReader(lease.Content, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        var retainedValue = (await reader.ReadLineAsync())!.Split(',')[1];

        Assert.Equal(finalState.ExpectedSourceValue, retainedValue);
        Assert.Equal("second", retainedValue);
    }

    [Fact]
    public async Task RetainedSource_DiscardedReplacementPreservesPreviousActiveSource()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var firstContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nfirst"));
        var first = await service.InspectCsvAsync(
            pipelineId, firstContent, "first.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(first.SourceReferenceId),
            CancellationToken.None));

        await using var replacementContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nreplacement"));
        var replacement = await service.InspectCsvAsync(
            pipelineId, replacementContent, "replacement.csv", options, CancellationToken.None);
        await service.DiscardAsync(
            Assert.IsType<Guid>(replacement.SourceReferenceId),
            CancellationToken.None);

        await using var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));
        using var reader = new StreamReader(lease.Content, leaveOpen: true);
        Assert.Equal("Id", await reader.ReadLineAsync());
        Assert.Equal("first", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task RetainedSource_PostSwapCleanupFailureKeepsReplacementActiveAndRecoverable()
    {
        var localStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var storage = new ReleaseThenFailStorage(
            localStorage,
            _clock.GetUtcNow().AddMinutes(-16),
            failureCount: 1);
        var service = CreateService(storage);
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        await using var originalContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\noriginal"));
        var original = await service.InspectCsvAsync(
            pipelineId, originalContent, "original.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(original.SourceReferenceId),
            CancellationToken.None));

        await using var replacementContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nreplacement"));
        var replacement = await service.InspectCsvAsync(
            pipelineId, replacementContent, "replacement.csv", options, CancellationToken.None);

        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(replacement.SourceReferenceId),
            CancellationToken.None));
        var orphanPath = Assert.IsType<string>(storage.LastFailedUploadPath);
        Assert.True(File.Exists(orphanPath));
        await using (var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None)))
        {
            using var reader = new StreamReader(lease.Content, leaveOpen: true);
            _ = await reader.ReadLineAsync();
            Assert.Equal("replacement", await reader.ReadLineAsync());
        }

        await service.PurgeOrphanedUploadsAsync(CancellationToken.None);

        Assert.False(File.Exists(orphanPath));
        await using var replacementLease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));
        using var replacementReader = new StreamReader(replacementLease.Content, leaveOpen: true);
        _ = await replacementReader.ReadLineAsync();
        Assert.Equal("replacement", await replacementReader.ReadLineAsync());
    }

    [Fact]
    public async Task RetireActiveSource_DoesNotDiscardAnotherPendingCommit()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var activeContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nactive"));
        var active = await service.InspectCsvAsync(
            pipelineId, activeContent, "active.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(active.SourceReferenceId),
            CancellationToken.None));
        await using var pendingContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\npending"));
        var pending = await service.InspectCsvAsync(
            pipelineId, pendingContent, "pending.csv", options, CancellationToken.None);

        await service.RetireActiveAsync(pipelineId, CancellationToken.None);

        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Csv, options, CancellationToken.None));
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(pending.SourceReferenceId),
            CancellationToken.None));
        await using var pendingLease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));
        using var reader = new StreamReader(pendingLease.Content, leaveOpen: true);
        _ = await reader.ReadLineAsync();
        Assert.Equal("pending", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task RetainedSource_ReplacementWaitsForActiveLeaseBeforeDeletingOldUpload()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var firstContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nfirst"));
        var first = await service.InspectCsvAsync(
            pipelineId, firstContent, "first.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(first.SourceReferenceId),
            CancellationToken.None));
        var firstLease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));

        await using var replacementContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nreplacement"));
        var replacement = await service.InspectCsvAsync(
            pipelineId, replacementContent, "replacement.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(replacement.SourceReferenceId),
            CancellationToken.None));
        Assert.Equal(2, Directory.EnumerateFiles(_root, "*.upload").Count());

        using (var reader = new StreamReader(firstLease.Content, leaveOpen: true))
        {
            Assert.Equal("Id", await reader.ReadLineAsync());
            Assert.Equal("first", await reader.ReadLineAsync());
        }

        await firstLease.DisposeAsync();
        Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));
        await using var replacementLease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));
        using var replacementReader = new StreamReader(replacementLease.Content, leaveOpen: true);
        Assert.Equal("Id", await replacementReader.ReadLineAsync());
        Assert.Equal("replacement", await replacementReader.ReadLineAsync());
    }

    [Fact]
    public async Task RetainedSource_ExpiresAndReturnsUnavailable()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id\n1"));
        var inspection = await service.InspectCsvAsync(
            pipelineId, content, "source.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(inspection.SourceReferenceId),
            CancellationToken.None));

        _clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Csv, options, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task RetainedSource_ExpirationDefersCleanupUntilAnActiveLeaseIsReleased()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id\nretained"));
        var inspection = await service.InspectCsvAsync(
            pipelineId, content, "source.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(inspection.SourceReferenceId),
            CancellationToken.None));
        var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Csv, options, CancellationToken.None));

        _clock.Advance(TimeSpan.FromMinutes(16));
        await service.PurgeExpiredAsync();

        Assert.Single(Directory.EnumerateFiles(_root, "*.upload"));
        using (var reader = new StreamReader(lease.Content, leaveOpen: true))
        {
            Assert.Equal("Id", await reader.ReadLineAsync());
            Assert.Equal("retained", await reader.ReadLineAsync());
        }

        await lease.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task PendingSourceSnapshot_RequiresOwnerExpiresAndReturnsCopies()
    {
        var pipelineId = Guid.NewGuid();
        var service = CreateService();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id,Name\n1,Ada"));
        var inspection = await service.InspectCsvAsync(
            pipelineId,
            content,
            "customers.csv",
            new SourceOptions { Delimiter = CsvDelimiter.Comma, CultureName = "en-US" },
            CancellationToken.None);
        var sourceReferenceId = Assert.IsType<Guid>(inspection.SourceReferenceId);

        Assert.Null(await service.GetPendingSourceAsync(
            Guid.NewGuid(), sourceReferenceId, CancellationToken.None));
        Assert.Null(await service.GetPendingSourceAsync(
            pipelineId, Guid.NewGuid(), CancellationToken.None));

        var snapshot = Assert.IsType<PendingSourceInspection>(await service.GetPendingSourceAsync(
            pipelineId, sourceReferenceId, CancellationToken.None));
        Assert.Equal(SourceType.Csv, snapshot.SourceType);
        Assert.Equal("en-US", snapshot.SourceOptions.CultureName);
        Assert.Equal(["Id", "Name"], snapshot.DetectedSchema.Select(field => field.Name));

        snapshot.SourceOptions.CultureName = "tr-TR";
        snapshot.DetectedSchema[0].Name = "Mutated";
        var secondSnapshot = Assert.IsType<PendingSourceInspection>(await service.GetPendingSourceAsync(
            pipelineId, sourceReferenceId, CancellationToken.None));
        Assert.Equal("en-US", secondSnapshot.SourceOptions.CultureName);
        Assert.Equal("Id", secondSnapshot.DetectedSchema[0].Name);

        _clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(await service.GetPendingSourceAsync(
            pipelineId, sourceReferenceId, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
    }

    [Fact]
    public async Task RetainedSource_RemoveClearsActiveAndPendingSourcesForPipeline()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var activeContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nactive"));
        var active = await service.InspectCsvAsync(
            pipelineId, activeContent, "active.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(active.SourceReferenceId),
            CancellationToken.None));
        await using var pendingContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\npending"));
        _ = await service.InspectCsvAsync(
            pipelineId, pendingContent, "pending.csv", options, CancellationToken.None);
        Assert.Equal(2, Directory.EnumerateFiles(_root, "*.upload").Count());

        await service.RemoveAsync(pipelineId, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_root, "*.upload"));
        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Csv, options, CancellationToken.None));
    }

    [Fact]
    public async Task RetainedXlsx_UsesSelectedWorksheetOptionsDuringLaterAcquisition()
    {
        var pipelineId = Guid.NewGuid();
        var service = CreateService();
        await using var workbook = Create(
            Sheet("First", Row(Text(1, "Wrong")), Row(Text(1, "ignored"))),
            Sheet("Data", Row(Text(1, "Id")), Row(Number(1, 7))));
        var stage = await service.StageXlsxAsync(
            pipelineId, workbook, "source.xlsx", CancellationToken.None);
        var options = new SourceOptions
        {
            WorksheetName = "Data",
            CultureName = "en-US",
            FirstRowIsHeader = true
        };

        var inspection = await service.InspectStagedXlsxAsync(
            pipelineId,
            Assert.IsType<Guid>(stage.StageId),
            "Data",
            CancellationToken.None,
            options);
        Assert.Equal(["Id"], inspection.Columns);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(inspection.SourceReferenceId),
            CancellationToken.None));

        await using var lease = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(pipelineId, SourceType.Xlsx, options, CancellationToken.None));
        Assert.True(lease.Content.CanRead);
        Assert.True(lease.Content.CanSeek);
    }

    [Fact]
    public async Task RunReservation_BlocksReplacementAndExpirationUntilRollbackRestoresSource()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id\nactive"));
        var inspection = await service.InspectCsvAsync(
            pipelineId, content, "active.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(inspection.SourceReferenceId),
            CancellationToken.None));
        var reservation = Assert.IsAssignableFrom<IWizardRunSourceReservation>(
            await service.ReserveForRunAsync(
                pipelineId,
                SourceType.Csv,
                options,
                CancellationToken.None));

        await using var replacementContent = new MemoryStream(Encoding.UTF8.GetBytes("Id\nreplacement"));
        var replacement = await service.InspectCsvAsync(
            pipelineId, replacementContent, "replacement.csv", options, CancellationToken.None);
        Assert.False(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(replacement.SourceReferenceId),
            CancellationToken.None));
        _clock.Advance(TimeSpan.FromMinutes(16));
        await service.PurgeExpiredAsync();

        Assert.True(File.Exists(reservation.StoredFilePath));
        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Csv, options, CancellationToken.None));

        await reservation.DisposeAsync();

        await using var restored = Assert.IsAssignableFrom<IWizardSourceLease>(
            await service.AcquireAsync(
                pipelineId, SourceType.Csv, options, CancellationToken.None));
        using var reader = new StreamReader(restored.Content, leaveOpen: true);
        Assert.Equal("Id", await reader.ReadLineAsync());
        Assert.Equal("active", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task RunReservation_TransferUsesExistingRunSourceOpenAndCleanupPath()
    {
        var pipelineId = Guid.NewGuid();
        var options = new SourceOptions { Delimiter = CsvDelimiter.Comma };
        var service = CreateService();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Id\nowned"));
        var inspection = await service.InspectCsvAsync(
            pipelineId, content, "customers.csv", options, CancellationToken.None);
        Assert.True(await service.ActivateAsync(
            pipelineId,
            Assert.IsType<Guid>(inspection.SourceReferenceId),
            CancellationToken.None));
        var reservation = Assert.IsAssignableFrom<IWizardRunSourceReservation>(
            await service.ReserveForRunAsync(
                pipelineId,
                SourceType.Csv,
                options,
                CancellationToken.None));
        var run = new EtlRun
        {
            Id = Guid.NewGuid(),
            PipelineId = pipelineId,
            OriginalFileName = reservation.OriginalFileName,
            StoredFilePath = reservation.StoredFilePath
        };

        reservation.TransferToRun();
        await reservation.DisposeAsync();

        Assert.Null(await service.AcquireAsync(
            pipelineId, SourceType.Csv, options, CancellationToken.None));
        var uploadStorage = new LocalUploadStorage(new UploadStorageOptions { RootPath = _root });
        var runStore = new LocalRunSourceFileStore(
            new UploadStorageOptions { RootPath = _root },
            uploadStorage);
        await using (var source = runStore.Open(run))
        using (var reader = new StreamReader(source, leaveOpen: true))
        {
            Assert.Equal("Id", await reader.ReadLineAsync());
            Assert.Equal("owned", await reader.ReadLineAsync());
        }

        await runStore.DeleteAsync(run, CancellationToken.None);
        Assert.False(File.Exists(run.StoredFilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<StructuredLogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                Entries.Add(new StructuredLogEntry(values));
            }
        }
    }

    private sealed record StructuredLogEntry(IReadOnlyList<KeyValuePair<string, object?>> Values)
    {
        public bool Has(string name, object? expected) => Values.Any(pair =>
            string.Equals(pair.Key, name, StringComparison.Ordinal)
            && string.Equals(pair.Value?.ToString(), expected?.ToString(), StringComparison.Ordinal));
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private static PipelineDefinition Pipeline(Guid pipelineId, SourceOptions options) => new()
    {
        Id = pipelineId,
        SourceType = SourceType.Csv,
        SourceOptions = options,
        ExpectedSchema =
        [
            new SourceFieldDefinition { Name = "Id", DataType = SourceFieldType.Integer },
            new SourceFieldDefinition { Name = "Value", DataType = SourceFieldType.String }
        ]
    };

    private sealed record CommittedSourceState(
        PipelineDefinition Pipeline,
        string ExpectedSourceValue);

    private sealed class BlockingAfterStoreStorage(IUploadStorage inner) : IUploadStorage
    {
        public TaskCompletionSource<StoredUpload> Stored { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken)
        {
            var upload = await inner.StoreAsync(content, originalFileName, cancellationToken);
            Stored.TrySetResult(upload);
            await Continue.Task.WaitAsync(cancellationToken);
            return upload;
        }

        public Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken) =>
            inner.DeleteAsync(upload, cancellationToken);

        public Task DeleteExpiredAsync(DateTimeOffset expiresBefore, CancellationToken cancellationToken) =>
            inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
    }

    private sealed class CancelAfterStoreStorage(
        IUploadStorage inner,
        CancellationTokenSource cancellationSource) : IUploadStorage
    {
        public StoredUpload? StoredUpload { get; private set; }

        public async Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken)
        {
            StoredUpload = await inner.StoreAsync(content, originalFileName, cancellationToken);
            cancellationSource.Cancel();
            return StoredUpload;
        }

        public Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken) =>
            inner.DeleteAsync(upload, cancellationToken);

        public Task DeleteExpiredAsync(DateTimeOffset expiresBefore, CancellationToken cancellationToken) =>
            inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
    }

    private sealed class BlockingStoredValidationService(IUploadValidationService inner) : IUploadValidationService
    {
        public TaskCompletionSource InspectionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<XlsxWorksheetStageResult> StoreForWorksheetSelectionAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken) =>
            inner.StoreForWorksheetSelectionAsync(content, originalFileName, cancellationToken);

        public async Task<UploadValidationResult> ValidateStoredAsync(
            StoredUpload upload,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            await using var activeRead = new FileStream(
                upload.StoredFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1];
            _ = await activeRead.ReadAsync(buffer, cancellationToken);
            InspectionStarted.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await inner.ValidateStoredAsync(upload, sourceType, sourceOptions, cancellationToken);
        }

        public Task<UploadValidationResult> StoreValidatedAsync(
            Stream content,
            string originalFileName,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) =>
            inner.StoreValidatedAsync(content, originalFileName, sourceType, sourceOptions, cancellationToken);
    }

    private sealed class FailingStoredValidationService(IUploadValidationService inner) : IUploadValidationService
    {
        public IOException InspectionFailure { get; } = new("Inspection read failed.");

        public Task<XlsxWorksheetStageResult> StoreForWorksheetSelectionAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken) =>
            inner.StoreForWorksheetSelectionAsync(content, originalFileName, cancellationToken);

        public async Task<UploadValidationResult> ValidateStoredAsync(
            StoredUpload upload,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(upload.StoredFilePath);
            var buffer = new byte[1];
            _ = await stream.ReadAsync(buffer, cancellationToken);
            throw InspectionFailure;
        }

        public Task<UploadValidationResult> StoreValidatedAsync(
            Stream content,
            string originalFileName,
            SourceType sourceType,
            SourceOptions sourceOptions,
            CancellationToken cancellationToken) =>
            inner.StoreValidatedAsync(content, originalFileName, sourceType, sourceOptions, cancellationToken);
    }

    private sealed class ReleaseThenFailStorage(
        IUploadStorage inner,
        DateTimeOffset orphanLastWriteTime,
        int failureCount) : IUploadStorage
    {
        private int _remainingFailures = failureCount;

        public int DeleteCallCount { get; private set; }
        public string? LastFailedUploadPath { get; private set; }

        public Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken) =>
            inner.StoreAsync(content, originalFileName, cancellationToken);

        public async Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken)
        {
            DeleteCallCount++;

            if (_remainingFailures-- <= 0)
            {
                await inner.DeleteAsync(upload, cancellationToken);
                return;
            }

            var content = await File.ReadAllBytesAsync(upload.StoredFilePath, cancellationToken);
            await inner.DeleteAsync(upload, cancellationToken);
            await File.WriteAllBytesAsync(upload.StoredFilePath, content, CancellationToken.None);
            File.SetLastWriteTimeUtc(upload.StoredFilePath, orphanLastWriteTime.UtcDateTime);
            LastFailedUploadPath = upload.StoredFilePath;
            throw new IOException("Simulated physical deletion failure after ownership release.");
        }

        public Task DeleteExpiredAsync(DateTimeOffset expiresBefore, CancellationToken cancellationToken) =>
            inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
    }

    private sealed class MissingBeforeDiscoveryThenFailDeleteStorage(
        IUploadStorage inner,
        DateTimeOffset orphanLastWriteTime) : IUploadStorage
    {
        private byte[]? _storedContent;

        public string? FailedUploadPath { get; private set; }

        public async Task<StoredUpload> StoreAsync(
            Stream content,
            string originalFileName,
            CancellationToken cancellationToken)
        {
            var upload = await inner.StoreAsync(content, originalFileName, cancellationToken);
            _storedContent = await File.ReadAllBytesAsync(upload.StoredFilePath, cancellationToken);
            File.Delete(upload.StoredFilePath);
            return upload;
        }

        public async Task DeleteAsync(StoredUpload upload, CancellationToken cancellationToken)
        {
            await inner.DeleteAsync(upload, cancellationToken);
            await File.WriteAllBytesAsync(
                upload.StoredFilePath,
                _storedContent ?? [],
                CancellationToken.None);
            File.SetLastWriteTimeUtc(upload.StoredFilePath, orphanLastWriteTime.UtcDateTime);
            FailedUploadPath = upload.StoredFilePath;
            throw new IOException("Simulated physical deletion failure after ownership release.");
        }

        public Task DeleteExpiredAsync(DateTimeOffset expiresBefore, CancellationToken cancellationToken) =>
            inner.DeleteExpiredAsync(expiresBefore, cancellationToken);
    }
}
