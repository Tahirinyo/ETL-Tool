# Project Progress

## Completed

- The MVP implementation is complete: CSV and modern XLSX ingestion, reusable pipelines, schema inference/mapping/remapping, ordered transformations, validation, preview, background batch execution, MongoDB bulk upserts, counters/statuses, error CSV, run history, and Docker Compose packaging.
- Day 13 release-candidate acceptance, including 100K execution acceptance and MongoDB resilience coverage, was completed.
- Day 14 packaging, technical documentation, and demo assets were completed.
- Day 15 final acceptance was completed. The demo configuration blocker was corrected in `01b4f1d`; fixture-backed acceptance verifies the documented clean and dirty row counters.
- Task 15.3 reconciled released capability claims, intentional MVP exclusions, and known non-blocking limitations with implementation and acceptance evidence.
- DB.13 added MongoDB source identity metadata and configured-connection database/collection discovery, including metadata/system-database exclusion and safe access failures. It does not include MongoDB schema inference, extraction, or UI integration.
- DB.14 added bounded, deterministic MongoDB source schema inference for top-level scalar fields, including null/missing handling, numeric widening, ObjectId mapping, safe conflict/unsupported-BSON failures, shared snapshot compatibility, and MongoDB source readiness validation. MongoDB extraction, Preview, and UI remain deferred.
- DB.15 added cursor-based MongoDB streaming extraction with configurable fetch size, deterministic `_id` ordering, safe BSON-to-row conversion (including exact IEEE-754 Double-to-CLR-decimal admission), execution-time schema-drift detection, immutable logical-source admission, and background-run integration. MongoDB Preview and UI integration remain deferred.
- DB.16 integrated MongoDB as a selectable pipeline source: configured-connection database/collection discovery, source-only UI binding, inferred schema handoff to the shared mapping flow, Preview source creation, safe source failures, and source/destination configuration separation. Existing schema comparison/remapping and execution-time drift guards apply to MongoDB sources.

## In Progress

- None.

## Blocked

- No known production release blocker remains. Non-blocking verification limitations are documented in `README.md` and `docs/Technical_Documentation.md`: stale/incorrect integration-test expectations, no live-browser reuse/remapping rehearsal, and no explicit restart-based named-volume retention confirmation.

## Next

- Package D Closeout cross-source regression and acceptance validation.
