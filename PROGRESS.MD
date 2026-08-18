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

## In Progress

- None.

## Blocked

- None.

## Next

- Characterize memory-controlled XLSX processing with the dedicated 100,000-row experiment (Task 3A.3).
