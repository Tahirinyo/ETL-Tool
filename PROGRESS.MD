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
- String conversion transformation handling, including invariant native-scalar formatting, null/string preservation, unsupported-value failures, application rule-service support, DI/registry integration, persisted-order composition, and focused unit coverage, has been implemented and verified.
- Integer and decimal conversion transformation handling, including explicit pipeline/source-culture parsing, `long`/`decimal` outputs, checked range and integrality rules, null preservation, row-level conversion failures, application rule-service support, DI/registry integration, persisted-order composition, and focused unit coverage, has been implemented and verified.
- Date conversion transformation handling, including explicit pipeline/source-culture and exact-format parsing, `DateTime` output, native-date and null preservation, offset rejection, failure-atomic row updates, application rule-service support, DI/registry integration, persisted-order composition, and focused unit coverage, has been implemented and verified.
- Conditional filter transformation handling, including explicit transformed/filtered outcomes, filter-on-match short-circuiting, ordinal string and typed `long`/`decimal`/`DateTime` comparisons, source-culture/date-format parsing, null/non-mutation semantics, application rule-service support, DI/registry integration, and focused unit coverage, has been implemented and verified.
- Selected-field deduplication, including explicit `Duplicate` outcomes, execution-scoped typed composite-key state, tentative commit/rollback semantics, ordered transformed-value evaluation, rule-service and DI support, and MongoDB `Fields` configuration round-trip coverage, has been implemented and verified.
- Culture/date parsing error consistency, including preserved format-versus-overflow taxonomy for numeric filter comparisons, deterministic shared date-parser parity, invariant/invalid-culture execution behavior, failure atomicity, and focused cross-consumer regression coverage, has been implemented and verified.
- Task 6B.2 client-side transformation reference validation, including mapped-output SourceField validation, Deduplicate SelectedFields validation, stale-reference feedback, type-change cleanup, submit blocking, and the nullable Deduplicate SourceField form-contract regression fix, has been implemented and verified.
- Task 6B.3 server-side transformation-reference validation regression coverage, including rejected non-Deduplicate and Deduplicate updates without persistence, preservation of prior valid rules, and MVC create/edit error redisplay with authoritative mapped-field choices, has been implemented and verified by focused and full unit suites.
- Task 7A.1 Required validation, including ordinal post-transformation field lookup, null/empty/whitespace/absent value failures, non-string value acceptance, reusable result/error contracts, registry/engine dispatch, DI registration, and focused unit coverage, has been implemented and verified.
- Task 7A.2 Email validation, including optional-value format checks, ordinal post-transformation field lookup, non-string rejection without coercion, custom/default messages, read-only behavior, registry/engine dispatch, DI registration, and focused unit coverage, has been implemented and verified.
- Task 7B.1 validation-rule creation, including MVC create/list screens, authoritative mapped-field options, type-specific server-side configuration validation, pipeline-derived upsert-key validation, aggregate-preserving persistence, and focused application/MVC/form coverage, has been implemented and verified.

## In Progress

- None.

## Blocked

- Full solution integration verification is limited in this environment: Testcontainers cannot access Docker at `npipe://./pipe/docker_engine`, and the two `PipelinesControllerBindingTests.TransformationPost_OmittedRequiredValueDoesNotCallRuleService` cases end their local HTTP response before antiforgery-token retrieval completes.

## Next

- Select the next approved MVP task.
