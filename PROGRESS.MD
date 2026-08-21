# Project Progress

## Completed

- Project scope and architecture have been approved.
- Initial .NET solution and six-project layer structure have been created and verified.
- Git workflow foundation, pull request checklist, and initial CI configuration have been established and locally verified.
- Initial pipeline, rule, source/schema/mapping, and ETL run domain contracts have been defined and verified.
- Application extraction contracts and the format-independent runtime row model have been defined and verified.
- Initial streaming CSV extraction with header mapping, logical row numbering, cancellation, and stream-ownership coverage has been implemented and verified.
- MongoDB metadata persistence for pipeline definitions, including secret-safe configuration, asynchronous CRUD semantics, Infrastructure-only BSON mapping, and isolated Testcontainers integration coverage, has been implemented and verified.
- Application pipeline CRUD services, including service-owned identity and timestamps, draft-level validation, update identity protection, and focused unit coverage, have been implemented and verified.
- Basic pipeline CRUD MVC pages, including narrow form models, anti-forgery-protected create/edit/delete flows, aggregate-preserving edits, Bootstrap navigation, and focused controller coverage, have been implemented and verified.
- CSV delimiter and source-culture support, including explicit comma/semicolon/tab parsing, invariant fallback, installed-specific-culture validation, and cross-delimiter parser regression coverage, has been implemented and verified.
- Streaming XLSX extraction and explicit worksheet selection, including native cell values, header and sparse-row semantics, logical numbering, cancellation, stream ownership, and focused integration coverage, has been implemented and verified.
- The opt-in 100,000-row extraction experiment has verified incremental CSV/XLSX consumption, collectible emitted rows, stable post-initialization live memory, early disposal, cancellation, and cleanup without requiring production extractor changes.
- Secure local upload storage, including application-controlled names, leaf-only filename metadata, a configurable private root, streaming copy, cancellation, partial-file cleanup, and filesystem integration coverage, has been implemented and verified.
- Server-side upload validation, including extension/source-type agreement, configurable byte and data-row limits, zero-byte rejection, production-extractor row semantics, structured rejection results, and safe rejected-file cleanup, has been implemented and verified.
- Pipeline-scoped CSV/XLSX source upload and inspection, including CSV delimiter binding, staged XLSX worksheet selection, continuous pending/registered/in-use upload ownership, failure-atomic per-stage expiry cleanup, primary-error-preserving orphan recovery, resilient periodic cleanup, source columns, bounded raw samples, temporary upload cleanup, and focused MVC/extraction coverage, has been implemented and verified.
- Schema detection and basic type suggestion, including ordered source schemas, conservative `Unknown`/`String`/`Integer`/`Decimal`/`Date` inference over the bounded inspection sample, configured culture/date-format handling, native XLSX values, successful-pipeline schema persistence, and focused unit/integration coverage, has been implemented and verified.
- Application-layer field mapping, including immutable prepared mapping plans, ordinal source/schema resolution, explicit configuration and missing-row failures, deterministic included-field ordering, and exact value/type preservation, has been implemented and verified with focused unit coverage.
- Field select, exclude, and rename MVC UI, including trusted-schema submission validation, reuse of application mapping validation, explicitly unsaved confirmation, and focused controller coverage, has been implemented and verified.
- Field-mapping persistence, including detached aggregate replacement, authoritative-schema reload, saved-state feedback, validation-safe failure behavior, and focused controller coverage, has been implemented and verified.
- Application-layer transformation handler registry and ordered engine, including explicit persisted-order execution, duplicate-order and ambiguous-registration rejection, handler preflight, sequential row composition, DI registration, and focused unit coverage, has been implemented and verified.
- Trim transformation handling, including exact post-mapping field lookup, string-only leading/trailing whitespace removal, null/non-string preservation, invalid-field failures, DI/registry registration, and focused unit coverage, has been implemented and verified.
- Upper and lower transformation handling, including invariant string casing, exact post-mapping field lookup, null/non-string preservation, invalid-field failures, DI/registry registration, Turkish-sensitive casing coverage, and focused unit coverage, has been implemented and verified.
- Default Value transformation handling, including null/empty-string replacement only, whitespace and non-string preservation, ordinal configuration lookup, explicit invalid-configuration failures, DI/registry registration, and focused ordering coverage with Trim, has been implemented and verified.
- Find/Replace transformation handling, including ordinal replace-all string behavior, explicit Find/Replace configuration validation, empty replacement deletion, null/non-string preservation, DI/registry registration, and focused unit coverage, has been implemented and verified.
- Persisted drag-and-drop transformation-rule ordering, including native MVC card dragging, anti-forgery-protected ordered-ID submission, exact pipeline-rule-set validation, one-based order normalization, aggregate-preserving persistence, and focused service/controller coverage, has been implemented and verified.

## In Progress

- None.

## Blocked

- Full integration-suite verification is environment-blocked: Docker/Testcontainers cannot access `npipe://./pipe/docker_engine`, and the existing web binding tests end their local HTTP responses prematurely. Task-specific source-inspection tests pass.

## Next

- Select the next approved transformation or pipeline task.
