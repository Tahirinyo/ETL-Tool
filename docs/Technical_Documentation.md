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
| `EtlTool.Infrastructure` | CSV/XLSX extractors, PostgreSQL and MongoDB streaming sources, MongoDB repositories, MongoDB and PostgreSQL loaders, local upload/run-source/error-report storage, and the in-process queue implementation. It references Application. |
| `EtlTool.Web` | MVC controllers, Razor views/view models, service composition in `Program.cs`, and hosted workers. It references Application and Infrastructure. Controllers coordinate HTTP only; they do not perform row processing or destination loading. |
| `EtlTool.UnitTests` | Isolated coverage of domain/application behavior and MVC coordination/view models. |
| `EtlTool.IntegrationTests` | Extractor, PostgreSQL/MongoDB source and destination, saved-connection, cross-database, reporting/storage, worker/queue, and selected MVC integration coverage. |

`PipelineDefinition` is the persisted definition of a pipeline: source options and expected schema, field mappings, transformation and validation rules, destination configuration, and the output-field upsert key. Database pipelines retain a saved-connection ID and logical PostgreSQL or MongoDB object identities, never a connection string. `EtlRun` persists an immutable execution snapshot, frozen saved-connection references, state, counters, timestamps, safe source metadata, system error summary, and an optional error-report reference.

At composition time, `Program.cs` registers the concrete CSV and XLSX extractors, logical-source metadata services, MongoDB repositories, `MongoBulkUpsertLoader`, `PostgreSqlBatchUpsertLoader`, local report storage, handler registries, ETL services, and hosted background services. The principal runtime boundaries include `IEtlSource`, `IRunSourceStore`, `IFileExtractorResolver`, `IPipelineDefinitionRepository`, `IEtlRunRepository`, `IDataLoader`, `IErrorReportWriter`, `IErrorReportStore`, and `IBackgroundJobQueue`.

### Saved connections and frozen revisions

The **Connections** workflow stores multiple named MongoDB and PostgreSQL connections in the MongoDB metadata database. Each connection configuration is protected with ASP.NET Core Data Protection before persistence. Replacing a configuration creates a new numbered revision; changing only its display name does not. The Data Protection key ring is stored under `SavedConnectionProtection:KeyRingPath`, which Docker Compose maps into the persistent `app-data` volume.

Pipelines store only the saved-connection ID and selected database/schema/table or database/collection identity. Metadata discovery and database-source Preview resolve the connection's current active revision. `RunAdmissionService` resolves the active source and destination revisions and copies `{ connection ID, provider, revision }` references into `EtlRunExecutionConfiguration`; `RunSourceStore` and the destination loaders then resolve those exact protected revisions. Editing a saved connection after admission therefore affects later previews and runs, but not an already queued or running execution. A saved connection cannot be deleted while a pipeline or active run references it.

`SavedConnectionProviderFactory` creates provider-specific runtime contexts for resolved revisions. Its internal PostgreSQL runtime-profile name is an implementation detail, not a user-configured profile. Legacy configured PostgreSQL profiles and the configured MongoDB runtime path remain compatibility paths; the current MVC source and destination workflow uses saved connections.

## ETL processing

Both preview and execution use `PipelineRowProcessor` to create an execution-scoped `PipelineRowProcessingSession`. A session prepares field mappings once, creates ordered transformation execution state once, and processes rows sequentially. Its processing order is fixed:

```text
IEtlSource.ReadAsync
  -> FieldMappingService.Apply
  -> TransformationExecution (ascending TransformationRule.Order)
  -> ValidationEngine.Validate
  -> upsert-key/within-session duplicate check
  -> valid row is eligible for IDataLoader
```

### Extract

All admitted runs are opened as an `IEtlSource` by `RunSourceStore` and yield `DataRow` values through `IAsyncEnumerable<DataRow>`. File-backed runs use `CsvFileExtractor` or `XlsxFileExtractor`; their extractor contract makes the caller responsible for the input stream. Legacy `.xls` remains unsupported.

PostgreSQL and MongoDB are logical sources: admission captures an immutable database/schema/table or database/collection identity, expected-schema snapshot, and saved-connection revision rather than a local source file. Before returning a logical source, `RunSourceStore` resolves that frozen revision, compares the live schema with the admitted snapshot, and requires remapping if it changed.

`MongoDbEtlSource` uses a deferred MongoDB cursor with an empty filter, deterministic ascending `_id` order, and configurable `MongoDb:SourceExecutionFetchSize` (default `1000`, valid range `1`-`10000`). It shapes each top-level document in expected-schema order, treating missing fields and BSON null as null. Supported conversions are string and ObjectId to string, Int32/Int64 to integer, exact numeric values to decimal, Boolean to Boolean, and BSON DateTime to UTC `DateTime`. Extra fields, incompatible or unsupported BSON types, and numeric values that cannot be represented exactly are run-level schema-change failures. Cancellation, early enumeration termination, and failures dispose the cursor; the shared MongoDB client is not disposed by the source.

### Map, transform, and validate

`FieldMappingService` applies active `FieldMapping` entries in their configured order to form a new output row. An active source and target field must both be present, source fields and output fields must each be unique, and at least one active mapping is required.

`TransformationEngine` orders the persisted `TransformationRule` list by `Order` and dispatches each rule through `TransformationHandlerRegistry`. The registry has one handler per `TransformationType`. The current handlers cover trim, upper/lower casing, string/integer/decimal/date conversion, default value, conditional filtering, find/replace, and selected-field deduplication. A row-level transformation `FormatException`, `OverflowException`, or `InvalidOperationException` becomes an invalid row with transformation error details; processing continues with the next source row.

Transformation results may short-circuit as `Filtered` or `Duplicate`; neither is validated or loaded. Deduplication state is session-scoped and is committed only after the row is ultimately valid. After validation, the session also requires a non-empty upsert-key value and keeps the first valid occurrence of each typed `UpsertKeyIdentity`; later matching rows are `Duplicate` and are not loaded.

`ValidationEngine` runs every configured rule against the transformed row and aggregates its errors. `ValidationHandlerRegistry` dispatches required, email, numeric-range, text-length-range, date-range, and upsert-key-required rules. Culture-aware conversion and validation use the pipeline's `SourceOptions`, rather than the process culture.

### Load and counters

`BatchOrchestrator` incrementally consumes the extractor and accumulates only valid rows into `EtlExecution:BatchSize` batches (the checked-in default is `1000`). It calls the supplied batch callback in source order, reports progress after successful full batches and excluded-row intervals, and emits a final completed snapshot.

Invalid rows are passed to the error-report callback; filtered and duplicate rows are excluded. `BatchExecutionProgress` and the final `BatchExecutionResult` track processed, valid, invalid, filtered, deduplicated, inserted, and updated rows. A readiness failure, target-access failure, unreadable source, cancellation, report failure, or batch-load failure is not treated as a successful row outcome.

`PipelineReadinessService` gates preview and execution. It checks supported/configured source options and schema, mappings, rule references/configuration, destination safety, and that the upsert key is an included mapped output field. PostgreSQL and MongoDB sources are both available for Preview and full execution. Schema inspection/comparison occurs before a source is committed for preview or run admission, and logical sources are checked again against the admitted snapshot when execution opens them.

## Preview and full execution

`PreviewService` resolves the same extractor, invokes the same readiness evaluation, and creates the same kind of row-processing session as `BatchOrchestrator`. Therefore mapping, persisted transformation order, transformation failures, filtering, validation, and session-scoped deduplication use the same implementation in both paths.

Preview stops after the first **100 source rows**. It returns structured `RowProcessingResult` values, from which the MVC view model derives the valid, invalid, filtered, and duplicate sample counts and row errors. It does not access a MongoDB target, call `IDataLoader`, create an `EtlRun`, write an error CSV, enqueue a job, or determine full-file outcomes.

Full execution processes the complete admitted source incrementally, checks target accessibility, sends valid rows to the loader in batches, persists run progress, and can produce an error report. Deduplication is intentionally execution-scoped in both cases: a preview's state is independent of the later full run.

## Background runs and run history

The execute action calls `RunAdmissionService`; it does not run ETL in the HTTP request. Admission captures a ready pipeline/source snapshot, creates a `Queued` `EtlRun` containing `EtlRunExecutionConfiguration`, persists it through `IEtlRunRepository`, and enqueues `BackgroundJob(runId)`. CSV/XLSX admission transfers a reserved run-owned file, while PostgreSQL and MongoDB admission records only immutable logical-source metadata and creates no source path. The bounded in-process queue uses `BackgroundJobQueue:Capacity` (checked-in default `100`); admission waits only up to `RunAdmission:QueueAdmissionTimeoutMilliseconds` (default `5000`). If queue admission fails, the persisted run is marked `Interrupted`.

`BackgroundJobWorker` is a hosted service with a single queue reader. It creates a scoped `IBackgroundJobExecutor` for each job; the registered executor is `EtlRunBackgroundJobExecutor`. The executor transitions an owned queued run to `Running`, reconstructs the source and destination from the immutable execution snapshot (including frozen saved-connection revisions), builds an error-report session, invokes `BatchOrchestrator`, persists monotonic progress through `MongoEtlRunRepository`, finalizes the report when applicable, and marks the run terminal. `RunsController` exposes a polling-friendly `GET /Runs/{runId}/Status` endpoint and run history/detail pages.

The worker links host shutdown and the per-run token from `ExecutionCancellationRegistry`; cancellation propagates into extractors, batching, loading, and report generation. The registry supplies cancellation infrastructure, but the current MVC application has no user-facing cancel-run endpoint. On shutdown, queue admission closes and queued jobs are reconciled; `AbandonedRunRecoveryService` also reconciles stale queued/running records at worker startup. Interrupted execution is recorded as `Interrupted`, not `Completed`.

For a normal system failure, the executor uses `Failed` if no target writes were confirmed, or `PartiallyCompleted` if prior inserts or updates were confirmed. `Completed`, `PartiallyCompleted`, `Failed`, and `Interrupted` are terminal; `Queued` and `Running` are non-terminal. A report produced before a later failure can be retained with that terminal run when finalization succeeds.

## Destination loading

The accepted product support matrix is intentionally narrower than the type abstractions might suggest:

| Source | MongoDB destination | PostgreSQL destination |
| --- | --- | --- |
| CSV | Supported | Outside the accepted matrix |
| XLSX | Supported | Outside the accepted matrix |
| PostgreSQL | Supported | Outside the accepted matrix |
| MongoDB | Outside the accepted matrix | Supported |

`DataLoaderResolver` selects the destination-specific loader from the immutable admitted run configuration. The batch orchestrator retains only confirmed inserted/updated results; a later system failure is `PartiallyCompleted` only when earlier writes were confirmed.

### MongoDB

The orchestrator validates the `MongoTarget` and checks access through `IMongoTargetAccessService` before extraction begins. `MongoTargetAccessService` rejects system databases and the configured metadata database, invalid database/collection names, and inaccessible targets.

For each valid batch, `MongoBulkUpsertLoader` builds `ReplaceOneModel<BsonDocument>` requests with `IsUpsert = true` and simple collation. The configured output upsert field is both the match field and the document key. Empty keys are rejected earlier by row processing; unsafe key field names (`.` or leading `$`) and incompatible mapped `_id` use are rejected by the loader. Repeating the same logical key therefore replaces the existing target document rather than inserting another one. The loader returns separate inserted and updated counts from the confirmed `BulkWrite` result.

Retries are deliberately narrow: `MongoDb:BulkWriteMaximumAttempts` and `MongoDb:BulkWriteRetryDelayMilliseconds` control retries only for MongoDB failures marked both `NoWritesPerformed` and `RetryableWriteError`. Cancellation is never retried. Other MongoDB or timeout failures become `BatchLoadException` with no confirmed result; the orchestrator carries confirmed counts into the failure progress, allowing the executor to distinguish `Failed` from `PartiallyCompleted`.

The `MongoDb` configuration section supplies the required application metadata connection and MongoDB retry/source defaults. Current pipeline destinations select a protected saved MongoDB connection; an admitted run carries its frozen revision to the loader. Neither configured nor saved connection strings are copied into pipeline or run documents.

### PostgreSQL

Current PostgreSQL source and destination workflows select a protected saved connection. A pipeline retains its saved-connection ID, database, schema, table, explicit output-to-column mappings, and upsert-key column; admission freezes the active connection revision for execution. Destination configuration discovers databases, schemas, tables, columns, and eligible unique/primary-key constraints before saving. The batch loader uses transactional `INSERT ... ON CONFLICT ... DO UPDATE` operations against the selected key column and reports confirmed inserts and updates separately. A rerun using the same selected key updates existing rows rather than creating duplicates. PostgreSQL batch retries are limited by `PostgreSql:BatchWriteMaximumAttempts` and `PostgreSql:BatchWriteRetryDelayMilliseconds`; cancellation is not retried. Named `PostgreSql:Profiles` remain supported for legacy-shaped persisted configurations but are not the normal saved-connection workflow.

## Error reports and file lifecycle

Only `Invalid` `RowProcessingResult` values enter `InvalidRowReportSession`; filtered and duplicate rows do not. The session starts a bounded producer/consumer writer only when the first invalid row occurs. `CsvErrorReportWriter` writes UTF-8-with-BOM CSV records, one per row error, with run ID, source row number, error stage/field/rule/type/message, effective upsert-key value, and original source-field values. It uses CSV escaping and prefixes untrusted values that could be interpreted as spreadsheet formulas.

`LocalErrorReportStore` writes to a generated partial file under `ErrorReportStorage:RootPath` (checked-in default `App_Data/error-reports`), then publishes it as the run-owned leaf filename `error-report-{runId}.csv`. The report reference persisted on `EtlRun` is verified against the requesting run before `RunsController` opens it for download. A missing, malformed, non-owned, or unavailable report returns not found; no user-supplied filesystem path is used.

No report is published when no invalid rows occur. Writer/cancellation failures abort the partial output. If terminal-run persistence fails after a report was published, the executor attempts to remove the report. After an executor-owned run has started, its `IEtlSource` is disposed on successful and failed execution. File-backed run sources are then deleted; logical PostgreSQL and MongoDB releases are no-ops because they own no run-local file. A source-cleanup failure is logged. Published reports remain available through run history until application-data retention/cleanup outside this runtime path removes them.

## Developer extension guide

These are internal code extension paths, not claims that the product currently supports additional source types or target systems.

### Add an extractor

The file-extractor contract is `IFileExtractor` in Application. An implementation belongs in `EtlTool.Infrastructure/Extraction`, provides its `SourceType`, implements deferred `ReadAsync` and `ReadHeadersAsync`, leaves the caller-owned input stream open, and observes cancellation. Register it as `IFileExtractor` in `EtlTool.Web/Program.cs`; `FileExtractorResolver` rejects duplicate registrations.

The file resolver accepts `Csv` and `Xlsx`; logical PostgreSQL and MongoDB sources are opened separately through `RunSourceStore`. Adding a genuinely new source type consequently requires coordinated changes to source options and snapshots, inspection and validation, readiness, admission and run-source opening, MVC configuration/binding, and test coverage. It is not an add-a-class-only plug-in mechanism. Follow the existing extractor/source tests for streaming, ordering, row numbering, cancellation, resource ownership, and schema-drift behavior.

### Add a transformation

Add a `TransformationType` value and an `ITransformationHandler` implementation in `EtlTool.Application/Transformations`. A handler declares the type it owns and returns `TransformationResult` for one mapped `DataRow`. Register it as `ITransformationHandler` in `Program.cs`; `TransformationHandlerRegistry` rejects more than one handler for a type, and `TransformationEngine` applies rules in ascending persisted `TransformationRule.Order`.

Also extend the places that define the supported configuration: `PipelineReadinessService`, `TransformationRuleService`, and the MVC rule form/controller when the new rule needs user configuration. A handler must preserve the existing row-processing contract: expected per-row format/overflow/configuration failures are represented as an invalid row by `PipelineRowProcessor`, while filter/duplicate outcomes short-circuit later stages. Add focused handler and engine/order tests, following the existing transformation test classes. No custom transformation-code execution exists.

### Add a loader

`IDataLoader` is selected by `DestinationType`; the current registrations are `MongoBulkUpsertLoader` and `PostgreSqlBatchUpsertLoader`. A change to either loader must preserve batch result, cancellation, upsert-key, and confirmed-counter semantics and include focused loader and batch-orchestration tests. Adding another target type is not a plug-in-only change: it requires destination configuration/access validation, pipeline/readiness contracts, DI composition, UI behavior, and acceptance evidence. This documentation does not advertise additional target support.

## Evidence, scope, and known limitations

The behaviors summarized above were checked against the concrete services and registrations named in this document, plus focused tests such as `PreviewServiceTests`, `PipelineRowProcessorTests`, `BatchOrchestratorTests`, `MongoBulkUpsertLoaderTests`, `PostgreSqlBatchUpsertLoaderTests`, `EtlRunBackgroundJobExecutorTests`, `BackgroundJobWorkerTests`, `CsvErrorReportWriterTests`, `LocalErrorReportStoreTests`, the run/preview MVC tests, and the PostgreSQL-to-MongoDB and MongoDB-to-PostgreSQL integration tests. DB.21 and DB.22 acceptance also exercised the accepted cross-database paths with real providers.

### Intentional MVP limits

The product supports CSV, modern XLSX, PostgreSQL, and MongoDB sources, and MongoDB/PostgreSQL destinations only for the accepted matrix above. It deliberately excludes legacy XLS, other database providers, unaccepted source/destination combinations, authentication/multi-tenancy, scheduled or distributed jobs, AI/fuzzy matching, custom code or regex validation, full-file dry runs, and cloud/production-SLA infrastructure. The configured MongoDB connection is required for application metadata; users may create multiple protected saved MongoDB and PostgreSQL connections for pipeline endpoints. Background work is an in-process, single-reader queue; there is no user-facing run-cancellation endpoint.

### Non-blocking verification limitations

- Deterministic PostgreSQL commit-ambiguity behavior is covered through the explicit batch-executor seam because the real provider has no practical commit-fault injection point for the acceptance suite.
- Compatible pipeline reuse and schema-change/remapping were not rehearsed in a live browser during final acceptance; automated MVC/application coverage provides the available evidence.
- Docker Compose named volumes are attached and startup/connectivity were verified, but persistence across an explicit stack restart was not manually confirmed.
