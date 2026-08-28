# Technical documentation

This document describes the ETL Tool MVP that is implemented in this repository. It complements the setup and operational guidance in the [README](../README.md); it does not describe roadmap items from the project plan as implemented features.

## Architecture

The application is a single ASP.NET Core MVC deployment with a pragmatic layered structure:

```text
EtlTool.Web
  -> EtlTool.Application -> EtlTool.Domain
  -> EtlTool.Infrastructure -> EtlTool.Application -> EtlTool.Domain

EtlTool.UnitTests         -> Domain, Application, Web
EtlTool.IntegrationTests  -> Infrastructure, Web
```

| Project | Implemented responsibility |
| --- | --- |
| `EtlTool.Domain` | Pipeline, rule, run, source-schema, mapping, and enum contracts. It has no project references. |
| `EtlTool.Application` | Use-case and ETL rules: mapping, transformation and validation engines, preview, batch orchestration, readiness checks, and contracts for persistence, extraction, loading, reporting, uploads, and queueing. It references Domain only. |
| `EtlTool.Infrastructure` | CSV/XLSX extractors, MongoDB repositories and loader, local upload/run-source/error-report storage, and the in-process queue implementation. It references Application. |
| `EtlTool.Web` | MVC controllers, Razor views/view models, service composition in `Program.cs`, and hosted workers. It references Application and Infrastructure. Controllers coordinate HTTP only; they do not perform row processing or MongoDB loading. |
| `EtlTool.UnitTests` | Isolated coverage of domain/application behavior and MVC coordination/view models. |
| `EtlTool.IntegrationTests` | Extractor, MongoDB, reporting/storage, worker/queue, and selected MVC integration coverage. |

`PipelineDefinition` is the persisted definition of a pipeline: source options and expected schema, field mappings, transformation and validation rules, destination database/collection, and the output-field upsert key. `EtlRun` persists a run snapshot, state, counters, timestamps, safe source metadata, system error summary, and an optional error-report reference.

At composition time, `Program.cs` registers the concrete CSV and XLSX extractors, MongoDB repositories and `MongoBulkUpsertLoader`, local report storage, handler registries, ETL services, and hosted background services. The principal runtime boundaries are `IFileExtractorResolver`, `IPipelineDefinitionRepository`, `IEtlRunRepository`, `IDataLoader`, `IErrorReportWriter`, `IErrorReportStore`, and `IBackgroundJobQueue`.

## ETL processing

Both preview and execution use `PipelineRowProcessor` to create an execution-scoped `PipelineRowProcessingSession`. A session prepares field mappings once, creates ordered transformation execution state once, and processes rows sequentially. Its processing order is fixed:

```text
IFileExtractor.ReadAsync
  -> FieldMappingService.Apply
  -> TransformationExecution (ascending TransformationRule.Order)
  -> ValidationEngine.Validate
  -> upsert-key/within-session duplicate check
  -> valid row is eligible for IDataLoader
```

### Extract

`CsvFileExtractor` and `XlsxFileExtractor` implement `IFileExtractor` and yield `DataRow` values through `IAsyncEnumerable<DataRow>`. `FileExtractorResolver` chooses one from `SourceType`. The extractor contract makes the caller responsible for the input stream and requires implementations to observe cancellation. Current supported source types are only `Csv` and `Xlsx`; `.xls` is not supported.

### Map, transform, and validate

`FieldMappingService` applies active `FieldMapping` entries in their configured order to form a new output row. An active source and target field must both be present, source fields and output fields must each be unique, and at least one active mapping is required.

`TransformationEngine` orders the persisted `TransformationRule` list by `Order` and dispatches each rule through `TransformationHandlerRegistry`. The registry has one handler per `TransformationType`. The current handlers cover trim, upper/lower casing, string/integer/decimal/date conversion, default value, conditional filtering, find/replace, and selected-field deduplication. A row-level transformation `FormatException`, `OverflowException`, or `InvalidOperationException` becomes an invalid row with transformation error details; processing continues with the next source row.

Transformation results may short-circuit as `Filtered` or `Duplicate`; neither is validated or loaded. Deduplication state is session-scoped and is committed only after the row is ultimately valid. After validation, the session also requires a non-empty upsert-key value and keeps the first valid occurrence of each typed `UpsertKeyIdentity`; later matching rows are `Duplicate` and are not loaded.

`ValidationEngine` runs every configured rule against the transformed row and aggregates its errors. `ValidationHandlerRegistry` dispatches required, email, numeric-range, text-length-range, date-range, and upsert-key-required rules. Culture-aware conversion and validation use the pipeline's `SourceOptions`, rather than the process culture.

### Load and counters

`BatchOrchestrator` incrementally consumes the extractor and accumulates only valid rows into `EtlExecution:BatchSize` batches (the checked-in default is `1000`). It calls the supplied batch callback in source order, reports progress after successful full batches and excluded-row intervals, and emits a final completed snapshot.

Invalid rows are passed to the error-report callback; filtered and duplicate rows are excluded. `BatchExecutionProgress` and the final `BatchExecutionResult` track processed, valid, invalid, filtered, deduplicated, inserted, and updated rows. A readiness failure, target-access failure, unreadable source, cancellation, report failure, or batch-load failure is not treated as a successful row outcome.

`PipelineReadinessService` gates preview and execution. It checks supported/configured source options and schema, mappings, rule references/configuration, destination safety, and that the upsert key is an included mapped output field. Schema inspection/comparison occurs before a source is committed for preview or run admission, so an incompatible source must be inspected and remapped before either operation can proceed.

## Preview and full execution

`PreviewService` resolves the same extractor, invokes the same readiness evaluation, and creates the same kind of row-processing session as `BatchOrchestrator`. Therefore mapping, persisted transformation order, transformation failures, filtering, validation, and session-scoped deduplication use the same implementation in both paths.

Preview stops after the first **100 source rows**. It returns structured `RowProcessingResult` values, from which the MVC view model derives the valid, invalid, filtered, and duplicate sample counts and row errors. It does not access a MongoDB target, call `IDataLoader`, create an `EtlRun`, write an error CSV, enqueue a job, or determine full-file outcomes.

Full execution processes the complete admitted source incrementally, checks target accessibility, sends valid rows to the loader in batches, persists run progress, and can produce an error report. Deduplication is intentionally execution-scoped in both cases: a preview's state is independent of the later full run.

## Background runs and run history

The execute action calls `RunAdmissionService`; it does not run ETL in the HTTP request. Admission captures a ready pipeline/source snapshot, creates a `Queued` `EtlRun` containing `EtlRunExecutionConfiguration`, persists it through `IEtlRunRepository`, and enqueues `BackgroundJob(runId)`. The bounded in-process queue uses `BackgroundJobQueue:Capacity` (checked-in default `100`); admission waits only up to `RunAdmission:QueueAdmissionTimeoutMilliseconds` (default `5000`). If queue admission fails, the persisted run is marked `Interrupted`.

`BackgroundJobWorker` is a hosted service with a single queue reader. It creates a scoped `IBackgroundJobExecutor` for each job; the registered executor is `EtlRunBackgroundJobExecutor`. The executor transitions an owned queued run to `Running`, opens the run-owned source, builds an error-report session, invokes `BatchOrchestrator`, persists monotonic progress through `MongoEtlRunRepository`, finalizes the report when applicable, and marks the run terminal. `RunsController` exposes a polling-friendly `GET /Runs/{runId}/Status` endpoint and run history/detail pages.

The worker links host shutdown and the per-run token from `ExecutionCancellationRegistry`; cancellation propagates into extractors, batching, loading, and report generation. The registry supplies cancellation infrastructure, but the current MVC application has no user-facing cancel-run endpoint. On shutdown, queue admission closes and queued jobs are reconciled; `AbandonedRunRecoveryService` also reconciles stale queued/running records at worker startup. Interrupted execution is recorded as `Interrupted`, not `Completed`.

For a normal system failure, the executor uses `Failed` if no target writes were confirmed, or `PartiallyCompleted` if prior inserts or updates were confirmed. `Completed`, `PartiallyCompleted`, `Failed`, and `Interrupted` are terminal; `Queued` and `Running` are non-terminal. A report produced before a later failure can be retained with that terminal run when finalization succeeds.

## MongoDB loading

`IDataLoader` currently has one registration: Infrastructure's `MongoBulkUpsertLoader`. The orchestrator validates the `MongoTarget` and checks access through `IMongoTargetAccessService` before extraction begins. `MongoTargetAccessService` rejects system databases and the configured metadata database, invalid database/collection names, and inaccessible targets.

For each valid batch, `MongoBulkUpsertLoader` builds `ReplaceOneModel<BsonDocument>` requests with `IsUpsert = true` and simple collation. The configured output upsert field is both the match field and the document key. Empty keys are rejected earlier by row processing; unsafe key field names (`.` or leading `$`) and incompatible mapped `_id` use are rejected by the loader. Repeating the same logical key therefore replaces the existing target document rather than inserting another one. The loader returns separate inserted and updated counts from the confirmed `BulkWrite` result.

Retries are deliberately narrow: `MongoDb:BulkWriteMaximumAttempts` and `MongoDb:BulkWriteRetryDelayMilliseconds` control retries only for MongoDB failures marked both `NoWritesPerformed` and `RetryableWriteError`. Cancellation is never retried. Other MongoDB or timeout failures become `BatchLoadException` with no confirmed result; the orchestrator carries confirmed counts into the failure progress, allowing the executor to distinguish `Failed` from `PartiallyCompleted`.

The MongoDB connection string is validated from the `MongoDb` configuration section at startup and is expected through an environment variable, user secrets, or another secret configuration provider. It is not stored in a pipeline or documented here.

## Error reports and file lifecycle

Only `Invalid` `RowProcessingResult` values enter `InvalidRowReportSession`; filtered and duplicate rows do not. The session starts a bounded producer/consumer writer only when the first invalid row occurs. `CsvErrorReportWriter` writes UTF-8-with-BOM CSV records, one per row error, with run ID, source row number, error stage/field/rule/type/message, effective upsert-key value, and original source-field values. It uses CSV escaping and prefixes untrusted values that could be interpreted as spreadsheet formulas.

`LocalErrorReportStore` writes to a generated partial file under `ErrorReportStorage:RootPath` (checked-in default `App_Data/error-reports`), then publishes it as the run-owned leaf filename `error-report-{runId}.csv`. The report reference persisted on `EtlRun` is verified against the requesting run before `RunsController` opens it for download. A missing, malformed, non-owned, or unavailable report returns not found; no user-supplied filesystem path is used.

No report is published when no invalid rows occur. Writer/cancellation failures abort the partial output. If terminal-run persistence fails after a report was published, the executor attempts to remove the report. After an executor-owned run has started, its source stream is disposed and its run source is deleted on both successful and failed execution; a source-cleanup failure is logged. Published reports remain available through run history until application-data retention/cleanup outside this runtime path removes them.

## Developer extension guide

These are internal code extension paths, not claims that the product currently supports additional source types or target systems.

### Add an extractor

The extractor contract is `IFileExtractor` in Application. An implementation belongs in `EtlTool.Infrastructure/Extraction`, provides its `SourceType`, implements deferred `ReadAsync` and `ReadHeadersAsync`, leaves the caller-owned input stream open, and observes cancellation. Register it as `IFileExtractor` in `EtlTool.Web/Program.cs`; `FileExtractorResolver` rejects duplicate registrations.

The existing resolver and `SourceType` enum explicitly accept only `Csv` and `Xlsx`. Adding a genuinely new source type would consequently require coordinated changes to the enum, resolver constraints, upload/source inspection and validation, readiness checks, MVC configuration/binding, and test coverage. It is not an add-a-class-only plug-in mechanism. Follow the existing `CsvFileExtractorTests` and `XlsxFileExtractorTests` patterns for streaming, headers, row numbering, cancellation, and caller stream ownership.

### Add a transformation

Add a `TransformationType` value and an `ITransformationHandler` implementation in `EtlTool.Application/Transformations`. A handler declares the type it owns and returns `TransformationResult` for one mapped `DataRow`. Register it as `ITransformationHandler` in `Program.cs`; `TransformationHandlerRegistry` rejects more than one handler for a type, and `TransformationEngine` applies rules in ascending persisted `TransformationRule.Order`.

Also extend the places that define the supported configuration: `PipelineReadinessService`, `TransformationRuleService`, and the MVC rule form/controller when the new rule needs user configuration. A handler must preserve the existing row-processing contract: expected per-row format/overflow/configuration failures are represented as an invalid row by `PipelineRowProcessor`, while filter/duplicate outcomes short-circuit later stages. Add focused handler and engine/order tests, following the existing transformation test classes. No custom transformation-code execution exists.

### Add a loader

The current `IDataLoader` seam is narrow and MongoDB-shaped: `UpsertBatchAsync` accepts `MongoTarget`, an upsert-key field, and rows. To alter MongoDB load behavior, implement `IDataLoader` in Infrastructure and replace the single registration in `Program.cs`; preserve batch result and cancellation/error semantics, then cover it with loader and batch-orchestration tests.

Adding a different target type is **not** a supported plug-in path. It would require a broader redesign of `IDataLoader`, target configuration/access validation, pipeline/readiness contracts, DI composition, and UI behavior. This documentation does not treat that unimplemented redesign as an available feature; the MVP target is MongoDB only.

## Evidence and limits

The behaviors summarized above were checked against the concrete services and registrations named in this document, plus focused tests such as `PreviewServiceTests`, `PipelineRowProcessorTests`, `BatchOrchestratorTests`, `MongoBulkUpsertLoaderTests`, `MongoBulkUpsertLoaderRetryTests`, `EtlRunBackgroundJobExecutorTests`, `BackgroundJobWorkerTests`, `CsvErrorReportWriterTests`, `LocalErrorReportStoreTests`, and the run/preview MVC tests.

Current deliberate limits include CSV and modern XLSX sources only, MongoDB-only loading, an in-process single-reader background queue, no distributed queue/scheduler, and no user-facing run-cancellation endpoint.
