# Project Progress

## Completed

- Fixed saved PostgreSQL and MongoDB source Preview and background-execution reconstruction: both resolve the admitted safe saved-connection reference through the runtime provider context, while pipeline/run persistence retains only safe logical metadata.
- The MVP implementation is complete: CSV and modern XLSX ingestion, reusable pipelines, schema inference/mapping/remapping, ordered transformations, validation, preview, background batch execution, MongoDB bulk upserts, counters/statuses, error CSV, run history, and Docker Compose packaging.
- Day 13 release-candidate acceptance, including 100K execution acceptance and MongoDB resilience coverage, was completed.
- Day 14 packaging, technical documentation, and demo assets were completed.
- Day 15 final acceptance was completed. The demo configuration blocker was corrected in `01b4f1d`; fixture-backed acceptance verifies the documented clean and dirty row counters.
- Task 15.3 reconciled released capability claims, intentional MVP exclusions, and known non-blocking limitations with implementation and acceptance evidence.
- DB.13 added MongoDB source identity metadata and configured-connection database/collection discovery, including metadata/system-database exclusion and safe access failures. It does not include MongoDB schema inference, extraction, or UI integration.
- DB.14 added bounded, deterministic MongoDB source schema inference for top-level scalar fields, including null/missing handling, numeric widening, ObjectId mapping, safe conflict/unsupported-BSON failures, shared snapshot compatibility, and MongoDB source readiness validation. MongoDB extraction, Preview, and UI remain deferred.
- DB.15 added cursor-based MongoDB streaming extraction with configurable fetch size, deterministic `_id` ordering, safe BSON-to-row conversion (including exact IEEE-754 Double-to-CLR-decimal admission), execution-time schema-drift detection, immutable logical-source admission, and background-run integration. MongoDB Preview and UI integration remain deferred.
- DB.16 integrated MongoDB as a selectable pipeline source: configured-connection database/collection discovery, source-only UI binding, inferred schema handoff to the shared mapping flow, Preview source creation, safe source failures, and source/destination configuration separation. Existing schema comparison/remapping and execution-time drift guards apply to MongoDB sources.
- Package E is complete: DB.17 made execution destination-neutral with admitted destination snapshots and safe loader resolution; DB.18 added validated PostgreSQL destination configuration and output-to-column mapping; DB.19 added transactional PostgreSQL batch upserts, retry classification, counters, and real-provider integration coverage; DB.20 acceptance verified insert/update/idempotency, source-key rejection and deduplication, retry/failure/partial-completion/cancellation propagation, safe errors, and committed-counter semantics. The only limitation is that deterministic commit-ambiguity coverage uses the explicit batch-executor seam because a real-provider commit-fault injection point is not available.
- DB.21 acceptance verified the MongoDB-source to PostgreSQL-destination path with real Testcontainers providers: shared Preview and background execution, mapping/transformation/validation/filter/deduplication, PostgreSQL insert/update idempotency, counters, error CSV, and completed run history.
- DB.22 cross-database regression acceptance verified CSV-to-MongoDB and XLSX-to-MongoDB through the real MongoDB loader, while revalidating the accepted PostgreSQL-to-MongoDB and MongoDB-to-PostgreSQL paths. Focused file/extractor, loader/lifecycle, cross-database, and full-unit regression groups passed; the prior 100K acceptance remains applicable because no performance-sensitive production code changed.
- DB.24 updated the README, technical documentation, and demo guide for the accepted database-to-database matrix: CSV/XLSX/PostgreSQL to MongoDB and MongoDB to PostgreSQL. It adds repeatable PostgreSQL-to-MongoDB and MongoDB-to-PostgreSQL demo scenarios using placeholder local configuration only; no production or test code changed.
- UX.1 polished the MVC pipeline workflow: destination saves return to the destination section, configuration actions now express forward/back hierarchy, and run progress/details provide direct routes to Preview, pipeline configuration, and Run History without changing execution admission semantics.
- CONN.2 blocking fixes enforce the database-source routing matrix for both saved and legacy-shaped source POSTs, and reject missing saved destination IDs in the current saved-connection workflow without mutating persisted pipeline state. Legacy runtime resolution remains covered.
- Fixed the MongoDB saved-source discovery regression: Configure Source now displays the existing safe metadata-discovery error rather than silently leaving dependent selectors disabled, and collection requests are bound to the selected database to avoid stale responses.
- Fixed the live saved-PostgreSQL destination persistence blocker: Edit POST now ignores fully blank browser-generated mapping placeholders while preserving validation for incomplete mappings; a Docker-backed HTTP-to-MongoDB reload regression verifies the persisted destination and Preview readiness.

## In Progress

- None.

## Blocked

- No known production release blocker remains. Non-blocking verification limitations are documented in `README.md` and `docs/Technical_Documentation.md`: stale/incorrect integration-test expectations, no live-browser reuse/remapping rehearsal, and no explicit restart-based named-volume retention confirmation.

## Next

- Select the next approved task.
