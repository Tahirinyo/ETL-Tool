# ETL Tool MVP - Updated Project Plan

## 1. Project Summary

This project is an ASP.NET Core MVC based ETL tool that allows developers and data personnel to clean, transform, validate, preview, and import data from CSV, modern Excel, PostgreSQL, or MongoDB through a visual interface. The accepted release matrix is CSV/XLSX/PostgreSQL to MongoDB and MongoDB to PostgreSQL.

The core MVP value proposition is:

> A user can save reusable mapping, transformation, and validation rules, apply them to compatible uploaded files or saved database sources, safely batch-upsert valid records into an accepted MongoDB or PostgreSQL destination, and receive detailed reports for invalid records.

The project is designed as a portfolio-quality MVP developed by two developers over 15 working days with AI coding tools such as Codex used as implementation assistants rather than autonomous owners of the codebase.

Current planning status:

- Days 1-15 record the original file-to-MongoDB MVP plan and completed release foundation.
- Later accepted database work added saved connections, PostgreSQL and MongoDB logical sources, PostgreSQL loading, frozen connection revisions, and the documented cross-database flows.
- Implementation, test, packaging, configuration, and documentation reconciliation are complete; final local closeout remains a separate stage.

---

## 2. Target Users

The first release targets:

- Developers
- Data personnel
- Technical staff responsible for system integration or data migration

A non-technical end-user experience, multi-user operation, and a customer-facing SaaS experience are outside the MVP scope.

---

## 3. Primary Demo Scenario

1. The user creates a new pipeline.
2. The user uploads a sample `.csv`/`.xlsx` file or selects a saved PostgreSQL/MongoDB source.
3. The system detects or infers the source columns and basic schema.
4. The user maps source columns to output fields.
5. The user adds transformation rules and orders them through the visual editor.
6. The user defines validation rules.
7. The user selects an accepted saved MongoDB or PostgreSQL destination and target object.
8. The user selects the output/upsert-key mapping required by that destination.
9. The system shows transformed preview rows and a sample error summary.
10. The user saves and runs the pipeline.
11. The system processes the admitted file or logical database source in background batches.
12. Valid records are upserted into the configured destination.
13. Invalid records are excluded from the target and written to a downloadable CSV report.
14. The user reviews duration, status, counters, and errors in run history.
15. The user later reuses the same pipeline with a compatible file or the saved logical database source.
16. If the source schema changed, the system requires remapping before execution.

---

## 4. Finalized Product Decisions

| Topic | Decision |
| --- | --- |
| Product target | Balanced, portfolio-quality MVP |
| Architecture target | Modular foundation that can be extended later |
| Sources | CSV, modern Excel (`.xlsx`), PostgreSQL, and MongoDB |
| Destinations | MongoDB and PostgreSQL within the accepted matrix |
| Accepted matrix | CSV/XLSX/PostgreSQL to MongoDB; MongoDB to PostgreSQL |
| Data capacity | Reliable batch processing up to 100,000 rows |
| Pipeline order | Fixed `Extract -> Map -> Transform -> Validate -> Load` |
| Visual editing | Ordered transformation list with drag-and-drop when available |
| Application metadata | One MongoDB connection supplied through application configuration |
| Pipeline connections | Multiple named MongoDB/PostgreSQL connections stored as protected, revisioned saved connections |
| Target selection | User selects a saved connection and MongoDB database/collection or PostgreSQL database/schema/table |
| Load strategy | MongoDB BulkWrite-style or transactional PostgreSQL batch upsert by the configured unique key |
| Invalid rows | Valid rows load; invalid rows are reported |
| Preview | Transformed sample data plus sample error summary |
| Reuse | Pipeline is saved; file pipelines accept compatible replacement files and database pipelines retain logical source identity |
| Run isolation | Admission freezes the active saved-connection revisions in the execution snapshot |
| Schema changes | Differences are shown and remapping is required |
| History | Run summary, duration, counters, errors, downloadable error CSV |
| User system | None; local or trusted environment only |
| Team and duration | 2 developers, 15 working days |
| AI coding tools | Used jointly with developer review and repository-grounded verification |

---

## 5. MVP Scope

### 5.1 Source Ingestion

- `.csv` upload
- `.xlsx` upload
- CSV delimiter selection: comma, semicolon, or tab
- Source culture/locale selection such as `tr-TR` or `en-US`
- Excel worksheet selection
- First row treated as column headers
- File extension, size, and row-count validation
- Incremental reading without loading the complete source file into memory
- Saved PostgreSQL source selection by connection, database, schema, and table
- Saved MongoDB source selection by connection, database, and collection
- Incremental PostgreSQL and MongoDB reading with deterministic source ordering
- Database schema inference/comparison and execution-time drift rejection

Legacy `.xls` support is outside the MVP.

### 5.2 Schema Detection and Field Mapping

- Detect source column names
- Suggest basic data types from sample values
- Keep or drop columns
- Rename source fields into output fields
- Map source fields to destination-neutral output fields
- Persist the expected source schema in the pipeline definition
- Compare a new source schema with the saved schema
- Show missing, new, and unmatched fields
- Require missing mappings to be corrected before execution

AI or fuzzy automatic schema matching is outside the MVP.

### 5.3 Transformation Rules

The MVP includes:

1. Trim
2. Uppercase
3. Lowercase
4. Convert to string
5. Convert to integer/decimal
6. Convert to date
7. Assign a default value to empty input
8. Filter rows by condition
9. Find and replace text
10. Remove duplicates by selected fields

Transformation rules can be created, edited, deleted, and reordered. Execution order is stored explicitly with an `Order` value.

Creating new fields by combining multiple fields is outside the MVP.

### 5.4 Validation Rules

The MVP includes:

- Required field
- Email format
- Numeric minimum/maximum
- Text minimum/maximum length
- Date minimum/maximum range
- Upsert field must not be empty

Validation runs after mapping and transformation. For example, trimming and numeric conversion occur before numeric range validation.

User-defined regex validation is outside the MVP.

### 5.5 Preview

- Preview the first 100 source rows
- Show transformed output fields in a table
- Show valid, invalid, and filtered counts for the preview sample
- Show concise row-level validation or transformation errors
- Show readiness/configuration errors before execution

Preview is not a full-file dry run. Full-file results are calculated only during execution.

### 5.6 Destination Loading

The accepted product matrix is deliberately narrower than the available source and destination type abstractions:

| Source | MongoDB destination | PostgreSQL destination |
| --- | --- | --- |
| CSV | Supported | Outside the accepted matrix |
| XLSX | Supported | Outside the accepted matrix |
| PostgreSQL | Supported | Outside the accepted matrix |
| MongoDB | Outside the accepted matrix | Supported |

- Select destinations through a saved provider-compatible connection.
- For MongoDB, select an allowed database/collection and an output-field upsert key, then write configured batches through BulkWrite-style upserts.
- For PostgreSQL, select a database/schema/table, map output fields to destination columns, select an eligible single-column primary/unique key, and use transactional `INSERT ... ON CONFLICT ... DO UPDATE` batches.
- Reject empty upsert keys, track confirmed inserted and updated records separately, and preserve idempotent reruns.
- Apply bounded retry only to eligible provider failures; cancellation is not retried.

The configured MongoDB connection is reserved for application metadata and compatibility behavior. Current pipeline destinations use protected saved connections. Metadata/system databases must not be selectable as MongoDB targets, and no database connection string or secret may be stored inside a pipeline or run snapshot.

### 5.7 Background Execution and Progress

- Run independently of the initiating HTTP request
- Support run states such as `Queued`, `Running`, `Completed`, `PartiallyCompleted`, and `Failed`
- Update processed-row and outcome counters as execution advances
- Expose status through polling-friendly MVC/application endpoints
- Use a simple in-process queue for the MVP
- Treat interrupted in-process execution clearly rather than silently reporting success
- Preserve cancellation behavior through asynchronous execution boundaries

Distributed queues, RabbitMQ, Kafka, and multi-instance worker coordination are outside the MVP.

### 5.8 Run History and Error Reporting

For each run, persist or expose the applicable information:

- Pipeline ID and name
- Safe source filename/reference
- Start and completion timestamps
- Total duration
- Status
- Total rows
- Processed rows
- Valid rows
- Invalid rows
- Filtered rows
- Deduplicated rows
- Inserted rows
- Updated rows
- System error summary
- Error report location when present

The downloadable error CSV must include at least:

- Source row number
- Upsert key value when available
- Invalid fields
- Error reasons
- Original row data

Successful rows should not receive verbose per-row logging because that would create unnecessary storage volume.

### 5.9 Saved Connections

- Create and manage multiple named MongoDB and PostgreSQL connections through the Connections UI.
- Protect connection configurations with ASP.NET Core Data Protection before saving them in application metadata.
- Keep revision history when credentials/configuration are replaced.
- Persist only saved-connection IDs and logical database object identities in pipeline definitions.
- Resolve the current revision for discovery and database-source Preview; freeze source and destination revisions when a run is admitted.
- Refuse deletion while a pipeline or active run references the saved connection.

---

## 6. Out of Scope

The MVP will not include:

- Free-form node canvas or node connections
- Scheduled or periodic pipeline execution
- REST API, JSON, XML, FTP, or database providers other than PostgreSQL and MongoDB
- Source/destination combinations outside the accepted matrix
- Authentication, roles, or authorization
- Multi-tenant SaaS architecture
- Million-row or distributed data processing
- RabbitMQ, Kafka, or distributed workers
- AI/fuzzy schema matching
- Field-combination expressions or user-defined code execution
- Regex-based user-defined validation
- Full-file loadless dry runs
- Undo/redo visual editing
- Data lineage
- Cloud deployment or production SLA infrastructure

These may be considered only after the mandatory MVP scope is complete.

---

## 7. Technical Architecture

### 7.1 Architecture Approach

The application uses a pragmatic **modular monolith**. It remains a single deployable application for fast delivery, while ETL responsibilities stay separated from MVC so additional extractors, transformations, validations, and loaders can be introduced later without rewriting the UI layer.

Target solution structure:

```text
EtlTool.sln
├── EtlTool.Web
│   ├── Controllers
│   ├── Views
│   ├── ViewModels
│   └── wwwroot
├── EtlTool.Application
│   ├── Pipelines
│   ├── Execution
│   ├── Transformations
│   ├── Validations
│   └── Interfaces
├── EtlTool.Domain
│   ├── Entities
│   ├── Enums
│   └── ValueObjects
├── EtlTool.Infrastructure
│   ├── FileExtraction
│   ├── MongoDB
│   ├── PostgreSql
│   ├── Sources
│   ├── Connections
│   ├── BackgroundJobs
│   └── Reports
├── EtlTool.UnitTests
└── EtlTool.IntegrationTests
```

The goal is clear separation of responsibilities, not unnecessary Clean Architecture ceremony.

### 7.2 MVC Responsibilities

- **Model:** pipeline definitions, rules, schema, and run history
- **View:** Razor Views for pipeline configuration, preview, execution status, history, and remapping
- **Controller:** thin HTTP boundary that validates input, calls Application services, and returns responses

ETL processing logic must not be implemented inside controllers or Razor Views.

### 7.3 UI Technology

- ASP.NET Core MVC
- Razor Views
- Bootstrap
- Small drag-and-drop JavaScript support for transformation ordering
- JavaScript polling for execution status

React, Vue, Angular, and free-form canvas libraries are outside the MVP.

### 7.4 Core Interfaces

```csharp
public interface IFileExtractor
{
    SourceType SourceType { get; }

    Task<IReadOnlyList<string>> ReadHeadersAsync(
        Stream stream,
        SourceOptions options,
        CancellationToken cancellationToken);

    IAsyncEnumerable<DataRow> ReadAsync(
        Stream stream,
        SourceOptions options,
        CancellationToken cancellationToken);
}

public interface ITransformationHandler
{
    TransformationType Type { get; }
    TransformationResult Apply(DataRow row, TransformationRule rule);
}

public interface IValidationHandler
{
    ValidationType Type { get; }
    ValidationResult Validate(DataRow row, ValidationRule rule);
}

public interface IDataLoader
{
    DestinationType DestinationType { get; }

    Task PrepareAsync(
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);

    Task<BatchLoadResult> UpsertBatchAsync(
        IReadOnlyList<DataRow> rows,
        PipelineDefinition pipeline,
        CancellationToken cancellationToken);
}
```

The execution/orchestration layer reads rows from the extractor, applies mapping, transformations, validation, batching, and loading in the fixed ETL order.

### 7.5 Data Processing Flow

```text
Source selection/inspection
  -> File validation or database metadata/schema validation
  -> Stream source rows
  -> Apply field mapping
  -> Apply ordered transformations
  -> Validate transformed row
  -> Invalid: error report
  -> Filtered: counter only
  -> Valid: add to batch
  -> Batch full: resolve destination loader and upsert
  -> Update run progress
```

The batch size must remain configurable. A value such as 1,000 rows may be used as a starting configuration but must not be hard-coded into the ETL algorithm.

---

## 8. Core Data Models

### PipelineDefinition

- `Id`
- `Name`
- `Description`
- `SourceType`
- `SourceOptions`
- `PostgreSqlSource` / `MongoDbSource`
- `ExpectedSchema`
- `FieldMappings`
- `TransformationRules`
- `ValidationRules`
- `DestinationType`
- MongoDB or PostgreSQL destination identity/mapping
- Saved source/destination connection IDs (without credentials)
- `UpsertKeyField`
- `CreatedAt`
- `UpdatedAt`

### TransformationRule

- `Id`
- `Type`
- `Order`
- `SourceField`
- `Configuration`

Prefer `Type -> Handler` dispatch rather than large controller condition blocks for each transformation type.

### ValidationRule

- `Id`
- `Type`
- `Field`
- `Configuration`
- `ErrorMessage`

### EtlRun

- `Id`
- `PipelineId`
- `Status`
- `OriginalFileName`
- `StoredFilePath`
- `StartedAt`
- `CompletedAt`
- `TotalRows`
- `ProcessedRows`
- `ValidRows`
- `InvalidRows`
- `FilteredRows`
- `DeduplicatedRows`
- `InsertedRows`
- `UpdatedRows`
- `SystemError`
- `ErrorReportPath`
- `ExecutionConfiguration` with immutable source/destination snapshots and frozen saved-connection references

These names describe the intended product contract. The repository implementation remains the source of truth for exact current type names and storage representation.

---

## 9. Error and Consistency Policy

### Row-Level Errors

A row with a transformation or validation failure must not be written to the target. Processing continues for other rows, and the failed row is included in the error report with its reasons.

### System-Level Errors

The following are run-level failures:

- Unreadable file or inaccessible logical database source
- Invalid or incomplete pipeline definition
- Saved-connection resolution or provider connection failure
- Inaccessible or invalid MongoDB/PostgreSQL target
- Batch write failure after the configured retry limit is exhausted

If earlier batches were committed before a later system-level failure, the run must preserve `PartiallyCompleted` semantics. Upsert behavior must allow a safe rerun without creating duplicate logical records.

### Duplicate Policy

- Explicit deduplication rules operate on their configured selected fields.
- If the same upsert key appears multiple times in one input, the first valid occurrence is kept deterministically and later occurrences are counted as duplicates.
- If the target already contains the same upsert key, the corresponding document/row is updated rather than duplicated.

### Temporary Files

- Uploaded source files are stored under unpredictable run-specific names.
- Source files are removed after successful or failed execution according to the run lifecycle.
- Error reports are retained according to run-history behavior.
- Orphaned temporary source files are cleaned up safely.
- Streams and other I/O resources are disposed on success, failure, and cancellation.

---

## 10. Important Problems and Chosen Solutions

| Problem | Chosen solution |
| --- | --- |
| RAM usage at 100,000 rows | Incremental row reading and configurable batch processing |
| Long HTTP requests/timeouts | In-process background queue, run ID, and polling |
| Columns change in a later file | Schema-difference flow plus mandatory remapping |
| Transformation order changes results | Explicit `Order`, UI ordering, and pre-run reference validation |
| Dirty rows stop the full import | Row quarantine plus downloadable error CSV |
| Rerunning the same logical data | User-selected unique field and idempotent upsert |
| Database secret leakage | Protected saved connections plus environment/secret configuration for application metadata; never store connection strings in pipeline or run data |
| Connection edited after run admission | Freeze the active source/destination revisions in the immutable run snapshot |
| Date/decimal format differences | Explicit pipeline source culture and date-format behavior |
| Destination batch failure | Provider-specific limited retry, correct run-level failure, `PartiallyCompleted` after committed batches |
| Old pipeline refers to changed source fields | Schema comparison plus rule-reference revalidation before execution |
| Verbose logs grow without value | Summary metrics and detailed reporting only for failed rows |

---

## 11. MVP Screens

1. **Dashboard / Pipeline List**
   - Pipeline name, source type, target, and latest run state
2. **Create/Edit Pipeline Wizard**
   - Source -> schema/mapping -> transformations -> validations -> target
3. **Transformation Editor**
   - Add, edit, delete, and reorder rules
4. **Preview**
   - First 100 transformed rows and sample error summary
5. **Run Screen**
   - Status, progress, and core counters
6. **Run History**
   - Pipeline runs and durations
7. **Run Detail**
   - Counters, system error, and error CSV download
8. **Schema Difference and Remapping**
   - Old/new column comparison and mapping correction
9. **Saved Connections**
   - Create, edit, and delete protected MongoDB/PostgreSQL endpoints and select them in database source/destination workflows

A separate decorative analytics dashboard is not a priority. The pipeline list should provide the useful summary information required for the MVP.

---

## 12. 15-Working-Day Development Plan

### 12.1 Role Distribution

**Developer A - ETL Engine focus**

- Extractors
- Transformation engine
- Validation engine
- Batch orchestration
- MongoDB loader/upsert
- Performance and core unit/integration verification

**Developer B - MVC and application-flow focus**

- Pipeline CRUD and repository flows
- Razor Views and ViewModels
- Upload, mapping, and transformation configuration UI
- Background job/status UI
- Run history and CSV report delivery
- Docker packaging and integration-facing work

The plan is task-based rather than ceremony-based. Not every task requires a full Plan -> Implementation -> Test -> Review -> Git chain.

### 12.2 Workflow Notation

The remaining backlog uses:

- `P` = Planning
- `I` = Implementation
- `T` = Test and validation
- `R` = Code review
- `G` = Local Git closeout
- `H` = High reasoning
- `M` = Medium reasoning
- `L` = Low reasoning

Assigned model families:

- `Sol` is reserved for the highest-value shared-core, persistence, security, and release-review work.
- `Terra` is the default for substantial planning, implementation, and validation work.
- `Luna` is used for mechanical, low-risk, documentation, demo-data, and local Git closeout work where appropriate.

### Days 1-9 - Completed Foundation

The pre-Day-10 backlog is intentionally not repeated task by task. The completed foundation covers the work required to reach the MongoDB persistence boundary:

- **Day 1:** solution/project skeleton, initial contracts, repository conventions, and project baseline
- **Day 2:** metadata persistence/pipeline CRUD foundation and extractor contracts
- **Day 3:** secure source upload plus CSV/XLSX extraction paths and source options
- **Day 4:** schema detection, field mapping, persistence of mappings, and related UI flow
- **Day 5:** ordered transformation engine foundation and initial transformation handlers/UI
- **Day 6:** remaining MVP transformations, culture-aware conversions, filtering, and deduplication behavior
- **Day 7:** validation engine, MVP validators, multi-error row behavior, and pipeline readiness rules
- **Day 8:** preview orchestration, transformed-row/error presentation, and end-to-end wizard integration around preview
- **Day 9:** batch execution orchestration, cancellation/progress contracts, in-process background execution, run status persistence, polling, and run progress UI

**Day 9 exit state:** the application can prepare and execute valid rows through the shared ETL path in background batches and report progress. The remaining critical persistence boundary begins on Day 10.

### Day 10 - MongoDB Bulk Upsert

Day 10 completes the MongoDB persistence boundary. The critical task is the `BulkWrite` loader.

#### Developer A - Task 10A.1: MongoDB Target Security and Access Controls

Scope:

- Validate allowed database and collection targets.
- Prevent metadata/system database selection as a user data target.
- Treat invalid or inaccessible targets as run-level failures.
- Ensure connection strings or secret values cannot leak into persisted pipeline data.

Workflow:

`I: Terra H | T: Terra H | R: Sol H | G: Luna L`

No separate planning stage. The problem boundary is already sufficiently defined by the project contracts.

#### Developer A - Task 10A.2: MongoDB BulkWrite Upsert Loader, Counters, and Limited Retry

This combines the previous loader, insert/update counter, and retry tasks.

Scope:

- Write configured batches with MongoDB `BulkWrite` operations.
- Use the user-selected upsert output field.
- Prevent duplicate target documents when the same logical data is rerun.
- Produce correct inserted and updated counters.
- Apply bounded retry to eligible MongoDB batch-write failures.
- Convert retry exhaustion into a run-level failure.
- Preserve partial-completion semantics if earlier batches were already committed.
- Keep cancellation behavior consistent with the existing execution/orchestration contract.

Workflow:

`P: Terra H | I: Sol H | T: Terra H | R: Sol H | G: Luna L`

This is one of the remaining tasks where Sol usage is intentionally protected.

#### Developer B - Task 10B.1: MongoDB Target and Upsert-Key Configuration UI

This combines database selection, collection selection, upsert-key selection, and user-facing connection/access errors.

Scope:

- Database selection
- Collection selection
- Upsert output-field selection
- Prevent invalid targets from being saved
- Present backend connection/access failures in understandable user-facing form

Workflow:

`I: Terra H | T: Terra M | G: Luna L`

No separate review stage is required.

**Day 10 output:** valid records can be written to MongoDB in batches with idempotent upsert behavior, accurate insert/update counters, and correct batch-failure semantics.

### Day 11 - Run History and Safe Error CSV

#### Developer A - Task 11A.1: Safe Error CSV Generation

This combines row-error contract review, CSV generation, and spreadsheet formula-injection protection.

Scope:

- Inspect the existing row-error model before introducing any new model.
- Reuse the current model if it already supports the required behavior.
- Include required run/row information in the error CSV.
- Write original row data safely.
- Prevent spreadsheet formula injection in generated cells.
- Preserve correct UTF-8 and CSV escaping behavior.
- Avoid unnecessary whole-file memory buffering for large error sets.

Workflow:

`I: Terra H | T: Terra H | R: Sol H | G: Luna L`

#### Developer B - Task 11B.1: Run History and Run Detail UI

This combines the previous run-history and run-detail tasks.

Scope:

- List pipeline runs.
- Show run status and duration.
- Show total, processed, valid, invalid, filtered, and duplicate counters.
- Show inserted and updated counters.
- Show the system-error summary.
- Show a download action when an error report exists.

Workflow:

`I: Terra M | G: Luna L`

No separate Plan, Test, or Review round is required. The implementation step should include only the focused MVC tests materially needed for the changed behavior.

#### Developer B - Task 11B.2: Error CSV Download Endpoint and Temporary-File Lifecycle

This combines error-report download and temporary-file cleanup.

Scope:

- Allow only the error report belonging to the requested run to be downloaded.
- Prevent path traversal and arbitrary-file download.
- Remove uploaded source files after completed or failed execution.
- Retain error reports according to the run-history policy.
- Support orphaned temporary-source cleanup.
- Dispose file and stream resources correctly.

Workflow:

`P: Terra M | I: Terra H | T: Terra H | R: Sol H | G: Luna L`

The review stage is retained because this task crosses file/path security and resource-lifecycle boundaries.

**Day 11 output:** complete run results are visible and invalid records can be downloaded as a safely generated and safely served CSV report.

### Day 12 - Schema Difference and Remapping

Day 12 is data-integrity critical and is kept as two larger shared tasks.

#### Developers A + B - Task 12.1: Schema Difference and Remapping Flow

This combines schema comparison, schema-difference UI, and remapping.

Scope:

- Compare the saved source schema with the new file schema.
- Identify missing fields.
- Identify new fields.
- Identify unmatched fields.
- Allow the user to repair the mapping.
- Persist the corrected mapping back to the pipeline.
- Do not add fuzzy or AI schema matching.

Workflow:

`P: Terra H | I: Terra H | T: Terra H | R: Sol H | G: Luna L`

#### Developers A + B - Task 12.2: Rule-Reference Revalidation and Execution Guard

This combines rule-reference revalidation with the guard that prevents execution before remapping is complete.

Scope:

- Revalidate transformation-rule field references after mapping changes.
- Revalidate validation-rule field references after mapping changes.
- Revalidate the upsert-field reference.
- Prevent pipeline execution when required references are missing or invalid.
- Tell the user which references are invalid.
- Never silently run rules that still refer to the old schema.

Workflow:

`P: Terra H | I: Terra H | T: Terra H | R: Sol H | G: Luna L`

**Day 12 output:** a saved pipeline can run against a changed-schema file only after the required remapping and reference repair are complete.

### Day 13 - Release Candidate Validation

Day 13 is no longer a feature-development day. Its purpose is to identify real verification gaps and produce a release candidate.

#### Shared - Task 13.1: Transformation, Validation, and Critical Application Coverage-Gap Audit

Replaces the old blanket requirement to rewrite or rerun every transformation and validation test.

Scope:

- Inspect existing test coverage first.
- Do not rewrite behavior that is already credibly tested.
- Identify missing boundary, failure, and regression cases.
- Add tests only for real coverage gaps.
- Check shared transformation/validation regression protection.

Workflow:

`T: Terra H`

If files change:

`G: Luna L`

#### Shared - Task 13.2: 100,000-Row Performance and Memory Acceptance Test

Combines the former performance and memory tasks.

Scope:

- Use the 100K acceptance dataset.
- Validate streaming/incremental processing.
- Validate batch processing behavior.
- Verify memory does not grow uncontrollably with file size.
- Verify the HTTP request is not held open for the execution duration.
- Report real measurements rather than unmeasured performance claims.

Workflow:

`T: Terra H`

If the test harness or files change:

`G: Luna L`

#### Shared - Task 13.3: MongoDB Resilience Acceptance Tests

Combines upsert-idempotency and partial-failure acceptance work.

Scope:

- Same logical data processed twice does not create duplicates.
- Insert -> rerun -> update behavior is correct.
- Batch failure is observable.
- Eligible transient failure can succeed after retry.
- Retry exhaustion fails correctly.
- A failure after an earlier committed batch preserves partial-completion semantics.
- `PartiallyCompleted` versus `Failed` is correct.
- Counters remain consistent with committed batches.

Workflow:

`T: Terra H`

#### Shared - Task 13.4: Critical MVP End-to-End Acceptance Tests

Scope:

- Clean CSV
- Dirty CSV
- Schema remap
- Error report
- Background execution/status
- Critical preview/run consistency paths
- Critical MVC/API integration boundaries

Do not add new tests where current tests already provide credible evidence.

Workflow:

`T: Terra H`

#### Shared - Task 13.5: Release Candidate Final Review

Scope:

- Day 10-13 changes
- Critical MVP acceptance criteria
- MongoDB persistence
- Error-report and file lifecycle
- Schema remapping
- Run statuses and counters
- Security-sensitive behavior
- Any open regression or release blocker

Workflow:

`R: Sol H`

If successful:

`G: Luna L`

#### Conditional Task 13.X: Acceptance Blocker or Bottleneck Fix

This task is created only if acceptance testing identifies a real blocker or bottleneck.

Model selection depends on the defect:

- Simple/local bug: `I: Terra H | T: Terra H`
- Shared-core, persistence, security, or data-loss bug: `P: Terra H | I: Sol H | T: Terra H | R: Sol H`
- After successful resolution: `G: Luna L`

**Day 13 output:** a release candidate that has passed the critical acceptance scenarios with any real blockers resolved or explicitly documented.

### Day 14 - Packaging and Documentation

#### Developer B - Task 14.1: Docker Packaging and Clean-Environment Installation

Combines Dockerfile, Docker Compose, installation README, and clean-install validation.

Scope:

- Application Dockerfile
- MongoDB + application Docker Compose
- Secret/configuration behavior
- No connection string embedded in the image or repository
- Required volumes, network, and configuration
- Clean-environment startup
- README installation/run instructions that match the real setup

Workflow:

`P: Terra M | I: Terra H | T: Terra H | R: Terra H | G: Luna L`

Sol is not required for this stage.

#### Developer A - Task 14.2: Technical Documentation Package

Combines architecture documentation, ETL engine documentation, and extension guidance.

Scope:

- Architecture overview based on the actual implementation
- `Extract -> Map -> Transform -> Validate -> Load`
- Preview/full-run relationship
- Background execution
- MongoDB loader
- Error report behavior
- How to add a new extractor
- How to add a new transformation
- How to add a new loader

Documentation must describe repository reality. Planned but unimplemented features must not be presented as completed capabilities.

Workflow:

`I: Terra M | G: Luna L`

No separate Test or Review stage is required.

#### Shared - Task 14.3: Demo Dataset and Demo Flow

Combines demo datasets and the demo script.

Scope:

- Clean sample dataset
- Dataset containing invalid rows
- Schema-change sample when useful
- Short, repeatable demo sequence
- Scenario that demonstrates the core MVP capabilities

Workflow:

`I: Luna M | G: Luna L`

#### Optional Task 14.4: Demo UI Polish

Open this task only if the actual demo flow exposes screens that look poor enough to harm usability or presentation.

Scope:

- Small layout adjustments
- Labels/text
- Visual consistency
- Demo usability

No new product feature may be introduced.

Workflow:

`I: Terra M | G: Luna L`

**Day 14 output:** a documented package with demo data that another developer can start and use in a clean environment.

### Day 15 - Final Acceptance and Release

#### Shared - Task 15.1: Final Acceptance and End-to-End Demo Rehearsal

Run the primary demo scenario against the real application from start to finish:

- Pipeline creation
- File upload
- Schema/mapping
- Transformation
- Validation
- Preview
- MongoDB target/upsert configuration
- Background run
- Progress/result
- MongoDB persistence
- Error CSV
- Run history
- Pipeline reuse
- Schema change/remapping

If performance-sensitive ETL code has not changed since Day 13, do not rerun the 100K acceptance test. If such code changed, include the 100K acceptance test in this task.

Workflow:

`T: Terra H | R: Sol H`

#### Conditional Task 15.2: Final Release Blocker Fix

Create only if Task 15.1 exposes a defect that actually blocks release.

For low/medium-risk defects:

`I: Terra H | T: Terra H | G: Luna L`

For persistence, security, data-loss, or shared-core defects:

`P: Terra H | I: Sol H | T: Terra H | R: Sol H | G: Luna L`

#### Shared - Task 15.3: MVP Scope and Known-Limitations Closeout

Combines scope verification and known-limitations documentation.

Scope:

- Confirm unimplemented features are outside the approved MVP scope rather than silently missing commitments.
- Record known environment/runtime limitations.
- Record known technical limitations that do not block release.
- Ensure README/docs do not claim capabilities the implementation does not provide.

Workflow:

`I: Terra M | G: Luna L`

#### Shared - Task 15.4: Final Local Git/Release Closeout

Scope:

- Inspect final repository state.
- Ensure the working tree is clean.
- Run required final lightweight Git checks.
- Perform local release commit/tag operations as appropriate.
- Keep remote GitHub operations separate.

Workflow:

`G: Luna L`

Do not spend Sol on release/Git mechanics.

**Day 15 output:** a working, tested, packaged, documented, and presentation-ready ETL Tool MVP.

### 12.3 Accepted Database Expansion After the Original 15-Day Plan

The original day-by-day backlog above records the file-to-MongoDB foundation. Subsequent approved packages extended that foundation without changing the shared `Extract -> Map -> Transform -> Validate -> Load` semantics:

- PostgreSQL and MongoDB became streaming logical sources with metadata discovery, schema inference/comparison, deterministic ordering, Preview, and background execution.
- PostgreSQL became a transactional batch-upsert destination with explicit output-to-column mappings and primary/unique-key admission.
- The accepted cross-database flows are PostgreSQL to MongoDB and MongoDB to PostgreSQL; CSV/XLSX to MongoDB remains the accepted file path.
- The Connections workflow added multiple named PostgreSQL/MongoDB connections, protected configurations, revision history, safe pipeline references, and execution-time revision freezing.
- Cross-database and real-provider acceptance verified mapping, ordered transformations, validation, filtering, deduplication, insert/update counters, idempotent reruns, error reports, status transitions, and failure/cancellation behavior for the accepted matrix.

This expansion supersedes earlier scope statements that described SQL sources, PostgreSQL destinations, or multiple MongoDB endpoints as future work. The historical task descriptions remain useful evidence of sequencing, but the final scope sections and repository implementation define the released product.

---

## 13. Codex Working Method

Codex should not attempt to build or rewrite the entire application in one pass. Work remains split into small, reviewable tasks with verification proportional to actual regression risk.

### 13.1 Source-of-Truth Order

Before consequential work, use this order:

1. `AGENTS.md`
2. This project plan
3. `PROGRESS.md`, when present
4. Current repository, tests, configuration, Git status, and relevant diff/history
5. Current user-approved task and task-specific plan

The repository is the final source of truth for what is actually implemented. Documentation or progress tracking alone does not prove completion.

### 13.2 Task Workflow

Use only the stages assigned to the task in Section 12.

- Do not force a Planning stage when the scope is already clear and localized.
- Do not create a separate Test round for low-risk UI/documentation tasks when focused verification can be performed during implementation.
- Do not create a separate Review round where the backlog explicitly omits it.
- Preserve dedicated Review for persistence, security, file/path lifecycle, schema integrity, shared-core behavior, and release-critical work.
- Keep local Git closeout mechanical and low-cost after implementation, validation, and required review are complete.

For tasks that do use separate stages:

1. **Planning:** inspect repository reality and produce a repository-grounded implementation plan without editing code.
2. **Implementation:** implement only the approved scope and the smallest necessary tests.
3. **Test/Validation:** verify observable behavior with a risk-based test scope and do not fix production defects unless explicitly instructed.
4. **Review:** inspect task, plan, implementation, diff, tests, and repository rules; report only concrete evidence-backed findings.
5. **Local Git closeout:** confirm the intended diff, run lightweight closeout checks, create the focused local commit, and keep remote GitHub operations separate.

### 13.3 AI Model Budgeting Principle

Model selection is part of the execution plan:

- Preserve Sol for the few tasks where deep reasoning materially reduces data-integrity, persistence, security, or release risk.
- Prefer Terra for most implementation, validation, and planning work.
- Prefer Luna for mechanical closeout, straightforward documentation/demo work, and other low-risk tasks.
- Do not upgrade model/reasoning level merely because a task has many files or because a full suite exists.

---

## 14. Test and Validation Strategy

Testing is risk-based. Every behavior change should have the smallest appropriate automated verification, but the full repository test suite should not run automatically after every task.

### 14.1 Per-Task Verification

Always when applicable:

- Run task-specific focused tests.
- Run `dotnet build EtlTool.sln --no-restore` after production-code changes.
- Run `git diff --check` before task closeout.

Risk levels:

**Low risk**

Examples: localized UI, ViewModel, small controller, documentation, navigation, presentation-only behavior, or narrowly isolated changes.

Default scope:

- Focused task tests
- Build after production-code changes
- No full unit suite unless repository evidence shows broader impact

**Medium risk**

Examples: application-service behavior, CRUD/update flows, mapping/rule configuration, or a contained MVC feature boundary.

Default scope:

- Focused task tests
- Directly affected feature/regression tests
- Build
- No full unit suite unless the actual change reaches shared behavior

**High risk / shared core**

Examples: shared parsers/contracts, transformation or validation engines, orchestration, persistence/upsert semantics, shared domain models, resource lifecycle, security-sensitive behavior, or broad call-site changes.

Default scope:

- Focused task tests
- Directly affected regression suites
- Full unit suite
- Relevant integration tests when an integration boundary changed
- Build

### 14.2 Integration-Test Focus

Run integration tests only when the task materially changes the corresponding boundary, including:

- CSV/XLSX extraction
- PostgreSQL/MongoDB logical-source extraction and schema drift
- MongoDB repository or bulk upsert
- PostgreSQL batch upsert
- Saved-connection protection and revision resolution
- Accepted cross-database execution
- Idempotent reruns
- Background execution/status transitions
- MVC/API binding when materially changed
- Error CSV generation and download
- Temporary-file cleanup/lifecycle
- Packaging, startup, Docker, or configuration

Do not repeatedly run unrelated integration suites just because they exist.

### 14.3 Full-Suite and Checkpoint Verification

Broader verification is justified at:

- End-of-day checkpoints when useful
- High-risk/shared-core tasks
- Day 13 release-candidate validation
- Major feature boundaries
- Final acceptance/release

Known environment-blocked suites should not be rerun after unrelated changes unless the current task could affect the blocked boundary.

Run `dotnet restore EtlTool.sln` when dependencies/project references changed, restore state is uncertain, or a broader checkpoint requires it. Do not require restore after every small task.

### 14.4 Release-Candidate Acceptance Scenarios

The release candidate must provide credible evidence for:

1. Clean CSV -> all valid rows are processed and loaded.
2. Dirty CSV -> valid rows load while invalid rows are reported.
3. Same logical data rerun -> no duplicate target records.
4. Insert followed by rerun -> expected update behavior and counters.
5. Changed source schema -> remapping is required before execution.
6. 100,000 rows -> incremental/batch behavior is measured without uncontrolled memory growth or HTTP-bound execution.
7. MongoDB/PostgreSQL batch failures -> provider-specific retry, exhaustion, `Failed`, and `PartiallyCompleted` semantics are correct.
8. Transformation ordering -> preview and full run remain consistent.
9. CSV and XLSX -> equivalent logical input follows the same ETL semantics.
10. Error CSV -> safe generation, escaping/formula protection, correct download authorization/path handling, and file lifecycle.
11. Background run -> status/progress/counters remain coherent through completion and failure paths.
12. PostgreSQL source -> MongoDB destination preserves the shared processing, batching, counters, schema-drift, and idempotency rules.
13. MongoDB source -> PostgreSQL destination preserves the shared processing, explicit column mapping, counters, error-report, and idempotency rules.
14. Saved connection edit -> a queued/running execution continues to use its admitted source/destination revisions.

---

## 15. Definition of Done

The MVP is complete only when:

- CSV, XLSX, PostgreSQL, and MongoDB sources work for the accepted matrix.
- Pipeline creation, editing, deletion, and reuse work.
- Mapping, all selected MVP transformations, and validations work.
- Transformation ordering is persisted and honored.
- Preview and full execution use the same mapping/transformation/validation semantics.
- The 100,000-row acceptance test passes with measured evidence appropriate to the environment.
- MongoDB and PostgreSQL batch upserts are idempotent for the defined logical key behavior.
- Inserted and updated counters are accurate.
- Invalid rows are not written to the destination and can be downloaded as a safe error CSV.
- MongoDB/PostgreSQL target safety and secret-handling requirements are satisfied.
- Saved connections are protected, pipelines retain no credentials, and admitted runs freeze provider-compatible source/destination revisions.
- PostgreSQL-to-MongoDB and MongoDB-to-PostgreSQL flows have real-provider acceptance evidence.
- Schema changes require remapping and invalid old rule references cannot execute silently.
- Run history, progress, statuses, and counters are coherent.
- Source temporary files and retained error reports follow the defined lifecycle safely.
- Critical services have appropriate unit/integration coverage based on regression risk.
- The application can start in a clean environment through the documented Docker Compose setup.
- README and technical documentation describe only implemented behavior.
- No connection string or secret is stored in the repository, pipeline data, or run snapshots.
- Known non-blocking limitations are documented.
- Final local Git/release closeout is complete and remote GitHub operations remain a separate step.

---

## 16. Scope-Cut Order if Time Becomes Tight

Core data correctness must be protected first. If schedule pressure requires simplification, reduce scope in this order:

1. Simplify dashboard/pipeline-list visual polish.
2. Show status and processed-row counts instead of a richer live percentage UI.
3. Reduce Excel worksheet selection behavior if the real implementation still allows that simplification safely.
4. Defer non-essential polish around find/replace or date-range validation only if release-candidate evidence shows they are incomplete and the approved MVP scope is explicitly revised.
5. Replace drag-and-drop with a simpler ordering control only if necessary for demo reliability.

Do not cut:

- CSV support
- Mapping
- Core transformation and validation engines
- Batch processing
- MongoDB upsert
- PostgreSQL upsert and the accepted cross-database paths
- Saved-connection protection and revision-safe execution
- Invalid-row separation
- Run result/status
- Schema-remap execution guard
- Safe error reporting
- Minimum credible automated regression coverage

---

## 17. Post-MVP Candidates

A reasonable post-MVP progression is:

1. REST API and JSON sources
2. Additional database providers
3. Scheduled pipeline execution
4. Source/destination combinations outside the accepted matrix
5. User and role system
6. Additional targets such as SQL Server
7. Distributed workers and a durable job queue
8. Advanced node-based visual editor

A node canvas should be introduced only when the product genuinely needs branching, multiple sources/targets, or conditional graph-style execution.
