# ETL Tool Repository Instructions

## Purpose

This repository contains a portfolio-quality MVP for importing CSV and modern Excel (`.xlsx`) data into MongoDB through an ASP.NET Core MVC application. Users define reusable pipelines that map, transform, validate, preview, and batch-upsert data while invalid rows are excluded and reported.

The MVP is being developed by two developers in 15 working days. Prefer a small, correct, demonstrable solution over production-scale infrastructure or speculative extensibility.

## Sources of Truth

Before planning or implementing a task, read sources in this order:

1. This `AGENTS.md` file.
2. `docs/ETL_Tool_MVP_Proje_Plani.md` for product scope, architecture, and acceptance requirements.
3. `PROGRESS.md`, when present, for completed, active, blocked, and next work.
4. The current repository, tests, configuration, and Git diff for the actual implementation state.
5. The current user-approved task and task-specific plan.

Do not infer that a feature is complete only because it appears in the project plan or `PROGRESS.md`. Verify the code and tests. If documentation and implementation disagree, report the conflict before making a consequential design decision.

## Working Method

- Work on one small, reviewable task at a time. Do not attempt to generate the entire application in one change.
- For complex or ambiguous tasks, inspect the repository and propose a plan before editing files.
- Each task must define its goal, scope, non-goals, affected layer, acceptance criteria, tests, and files that must not change.
- Implement only the approved task. Do not add adjacent features, speculative abstractions, or unrelated refactors.
- Preserve user changes and unrelated work in a dirty working tree. Never revert or overwrite them without explicit approval.
- Follow existing repository patterns before introducing new ones.
- Do not add a production dependency unless it is necessary for the approved task. Explain the need and trade-off first.
- If an ambiguity affects data correctness, persistence semantics, security, or public behavior, stop and ask rather than guessing.
- After implementation, run relevant checks, review the diff, and report what changed and what could not be verified.
- Do not claim a command or test passed unless it was actually run successfully.

## Target Architecture

Use a pragmatic modular monolith with these responsibilities:

- `EtlTool.Domain`: entities, enums, and value objects. It must not depend on Application, Infrastructure, or Web.
- `EtlTool.Application`: ETL orchestration, pipeline use cases, transformations, validations, and interfaces. Keep it independent of MVC and concrete infrastructure.
- `EtlTool.Infrastructure`: CSV/XLSX extraction, MongoDB persistence and loading, background jobs, temporary-file handling, and error reports.
- `EtlTool.Web`: ASP.NET Core MVC controllers, Razor Views, ViewModels, Bootstrap UI, and progress polling.
- `EtlTool.UnitTests`: isolated tests for transformation, validation, mapping, schema comparison, deduplication, and readiness rules.
- `EtlTool.IntegrationTests`: extractor, MongoDB, bulk-upsert, background-job, and error-report integration tests.

Keep controllers thin: validate HTTP input, call Application services, and return a response. Never place ETL processing, transformation, validation, or MongoDB loading logic in controllers or Razor Views.

Do not apply unnecessary Clean Architecture ceremony. Add interfaces and abstractions only where they preserve these boundaries, enable testing, or represent an actual extension point.

## Required ETL Behavior

Preserve this processing order:

`Extract -> Map -> Transform -> Validate -> Load`

- Support CSV and `.xlsx`; legacy `.xls` is outside the MVP.
- Process input incrementally. Do not load an entire source file into memory.
- Keep batch size configurable; do not hard-code it into the ETL algorithm.
- Apply transformations in their persisted explicit order.
- Run validation after mapping and transformation.
- Use the same mapping, transformation, and validation engine for preview and full execution.
- Preview processes the first 100 rows; it is not a full-file dry run.
- Continue processing after row-level transformation or validation failures. Do not load invalid rows; include them in the error report.
- Treat unreadable files, invalid pipeline configuration, inaccessible MongoDB targets, and exhausted batch-write retries as run-level failures.
- Use `PartiallyCompleted` when earlier batches were committed but a later system-level failure stops the run.
- Use `BulkWrite`-style batch upserts with the user-selected unique output field.
- Reject empty upsert keys.
- Re-running the same logical data must not create duplicate target documents.
- Within one input, keep the first valid row for a repeated upsert key and count later occurrences as duplicates.
- Persist and report inserted, updated, invalid, filtered, and duplicate counts accurately.
- Detect source-schema changes and require remapping before execution.

## Data, File, and Secret Safety

- Never commit MongoDB connection strings, credentials, secrets, or uploaded source data.
- Read secrets from environment variables or an approved secret configuration mechanism.
- Store uploaded files under unpredictable run-specific names; never trust the original filename as a storage path.
- Validate extension, size, format, and relevant upload metadata before processing.
- Dispose streams and other I/O resources reliably, including on cancellation and exceptions.
- Remove temporary source files after successful or failed execution. Retain error CSV files only according to run-history behavior.
- Avoid logging complete successful rows or secrets. Store summary metrics and error-row details only where required.
- Do not swallow exceptions or convert failures into misleading success states.

## C# and .NET Guidelines

- Use the repository's selected .NET target and existing language/version conventions; do not upgrade them as part of an unrelated task.
- Prefer dependency injection and options-based configuration over service location or static mutable state.
- Use async APIs for I/O and pass `CancellationToken` through asynchronous boundaries where supported.
- Avoid `.Result`, `.Wait()`, fire-and-forget tasks, and blocking I/O in request or background-processing paths.
- Parse dates and numbers using the pipeline's explicit source culture, such as `tr-TR` or `en-US`; do not depend on the machine's current culture.
- Prefer clear domain names and small cohesive methods over generic helper classes.
- Add comments only when they explain a non-obvious rule, invariant, or trade-off.

## Testing and Verification

Every behavior change must include or update the smallest appropriate automated tests.

Required unit-test focus:

- Every transformation handler.
- Every validation handler.
- Culture-aware date and numeric conversions.
- Mapping and schema comparison.
- Deduplication and empty upsert-key behavior.
- Pipeline-readiness checks.

Required integration-test focus when relevant:

- Streaming CSV/XLSX extraction.
- MongoDB repository and bulk upsert.
- Idempotent reruns.
- Background-run status transitions.
- Error CSV generation.
- Temporary-file cleanup.

When the solution exists, use the repository's documented commands. Unless the repository defines different commands, the expected baseline is:

```bash
dotnet restore EtlTool.sln
dotnet build EtlTool.sln --no-restore
dotnet test EtlTool.sln --no-build
```

Use Docker Compose validation when the task changes application startup, MongoDB integration, configuration, or packaging. If an external dependency prevents a check, report the exact missing dependency and run all unaffected checks.

For performance-sensitive ETL changes, verify incremental processing and consider the 100,000-row acceptance dataset. Do not replace evidence with an unmeasured claim about performance.

## Code Review Rules

Prioritize findings that can cause:

- Data loss or invalid rows being loaded.
- Incorrect or non-idempotent MongoDB upserts.
- Preview/full-run inconsistencies.
- Incorrect transformation ordering or culture-dependent conversion.
- Incorrect run status or counters.
- Excessive memory use or long HTTP-bound processing.
- Leaked streams, temporary files, connections, or background tasks.
- Secret exposure, unsafe file paths, or insufficient upload validation.
- Missing regression coverage for changed behavior.

Report concrete, actionable findings with file references and behavioral impact. Do not report personal style preferences unless they violate an established repository rule or create a material maintenance risk.

## MVP Non-Goals

Do not introduce these unless the user explicitly changes the approved scope:

- Authentication, authorization, roles, or multi-tenancy.
- React, Vue, Angular, or a free-form node canvas.
- SQL, REST, JSON, XML, FTP, or non-MongoDB targets.
- Multiple MongoDB connection profiles.
- RabbitMQ, Kafka, distributed workers, or multi-instance coordination.
- Scheduled pipelines.
- AI/fuzzy schema matching.
- User-defined code execution or regex validation.
- Full-file dry runs, data lineage, undo/redo, cloud deployment, or production SLA infrastructure.

## Progress Tracking

- Read `PROGRESS.md` before selecting or planning the next task when the file exists.
- Update it only after the task's acceptance criteria are met and relevant checks pass, unless the user explicitly requests an interim status update.
- Never mark partially implemented or unverified work as completed.
- Keep it concise under `Completed`, `In Progress`, `Blocked`, and `Next` headings.
- Record blockers and verification gaps explicitly.
- Do not use `PROGRESS.md` as a substitute for tests, Git history, or the project plan.

## Definition of Done for a Task

A task is complete only when:

- The approved scope and acceptance criteria are satisfied.
- Architecture boundaries and ETL invariants remain intact.
- Relevant tests were added or updated and pass.
- The solution builds, or any environment-specific blocker is reported precisely.
- The diff contains no unrelated changes, secrets, generated clutter, or temporary data.
- Error behavior and edge cases were considered.
- Documentation and `PROGRESS.md` were updated when the task changes them.
- The final response lists changed files, commands run, results, limitations, and any follow-up work.
