# Task 3A.3 — 100,000-Row Extraction Memory Experiment

## Result

The Release-mode experiment completed successfully for both CSV and XLSX. It found no evidence that emitted `DataRow` instances are retained as enumeration advances and no extraction-memory blocker for the 100,000-row MVP target.

- CSV live managed memory remained approximately 1.84 MiB from the first row through row 100,000.
- XLSX live managed memory reached approximately 16.30 MiB before the first row was emitted, remained flat through row 100,000, and returned to approximately 1.84–1.85 MiB after reader disposal.
- The XLSX first-row increase is consistent with ExcelDataReader loading workbook metadata, styles, and the complete shared-string table during reader initialization. It did not continue growing as worksheet rows were emitted.
- Weak references to the rows emitted at 10,000 and 50,000 were collectible after advancing to 50,000 and 100,000 respectively in every measured run.
- Early disposal and cancellation completed cleanly for both formats. The caller-owned input streams remained usable, and the temporary XLSX file was deleted after the experiment.

This is evidence about the tested seven-column fixtures and runtime environment. It is not a production memory SLA or a claim about arbitrarily wide rows or cell values.

## Environment and Fixtures

Measured on 2026-08-18 in an isolated xUnit performance-test invocation.

| Item | Value |
| --- | --- |
| OS | Microsoft Windows 10.0.26200, x64 process |
| Runtime | .NET 10.0.11 |
| Configuration | Release |
| CsvHelper | Package 33.1.0; assembly 33.0.0.0 |
| ExcelDataReader | 3.9.0 |

| Format | Data rows | Columns | Input bytes | SHA-256 |
| --- | ---: | ---: | ---: | --- |
| CSV | 100,000 | 7 | 8,056,745 | `BB40C811DAC1C7F1D1FE12C9CCC8FB7FC1835A89EA369779CA616DC06FD1B9C8` |
| XLSX | 100,000 | 7 | 7,313,808 | `73D9E5633007EEC5802F5DE26992C98F80BAC53379793D3CB8B3A90068F9A2D9` |

The CSV is the tracked `test-data/performance-100k.csv`. The XLSX is generated directly to a temporary `FileStream` with `ZipArchive` and forward-only `XmlWriter` calls. It contains deterministic ZIP timestamps and formula-derived shared-string indices; no worksheet DOM, in-memory workbook, or all-row collection is created.

## Method

The test performs a small-fixture warm-up, generates and closes the temporary XLSX, forces a compacting full GC, and then runs three full enumerations per format. The consumer retains only the async enumerator's current row, scalar counters, six checkpoint records, and two weak references.

Checkpoints are captured at baseline, first row, rows 10,000, 50,000, and 100,000, and after disposal. Each checkpoint forces a full GC before recording:

- total allocated bytes, which measure cumulative allocation churn and are expected to grow with row count;
- live managed bytes, heap size, committed memory, and fragmentation, which characterize retained managed memory;
- process working set and private bytes, which are diagnostic snapshots affected by runtime and test-runner noise;
- cumulative bytes read, GC collection counts, and elapsed time.

Forced collections perturb elapsed time, so timings are descriptive only. XLSX performs seeking within its ZIP container; cumulative bytes read can therefore exceed the compressed file length and is not interpreted as a unique-byte count.

## Measurements

The following checkpoint values are medians across three repetitions. MiB values use 1,048,576 bytes.

### CSV

| Checkpoint | Live managed MiB | Total allocated MiB | Managed heap MiB | Working set MiB | Private MiB | Bytes read | Elapsed ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 1.81 | 0.01 | 1.80 | 84.36 | 37.45 | 0 | 0 |
| First row | 1.84 | 0.05 | 1.83 | 84.32 | 37.39 | 5,120 | 0 |
| 10,000 | 1.84 | 8.74 | 1.83 | 84.32 | 37.39 | 778,240 | 50 |
| 50,000 | 1.85 | 43.81 | 1.83 | 84.50 | 37.39 | 4,014,080 | 213 |
| 100,000 | 1.84 | 87.65 | 1.83 | 85.27 | 37.98 | 8,056,745 | 418 |
| Disposed | 1.84 | 87.66 | 1.83 | 85.28 | 37.98 | 8,056,745 | 429 |

At row 100,000, live managed memory was 1.84/1.84/1.84 MiB and total allocation was 87.65/87.65/87.66 MiB min/median/max. Linear allocation is expected because each emitted row and its field values are newly allocated; the flat live-memory curve and collectible sentinels show that those rows are not retained.

### XLSX

| Checkpoint | Live managed MiB | Total allocated MiB | Managed heap MiB | Working set MiB | Private MiB | Bytes read | Elapsed ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 1.84 | 0.01 | 1.83 | 88.96 | 38.98 | 0 | 0 |
| First row | 16.30 | 71.77 | 18.74 | 95.18 | 45.19 | 862,474 | 235 |
| 10,000 | 16.30 | 90.47 | 18.72 | 100.46 | 50.36 | 1,493,258 | 339 |
| 50,000 | 16.30 | 167.38 | 18.72 | 100.32 | 50.29 | 4,090,122 | 587 |
| 100,000 | 16.30 | 264.27 | 16.29 | 100.32 | 50.29 | 7,325,582 | 892 |
| Disposed | 1.84 | 264.28 | 1.84 | 89.02 | 38.98 | 7,325,582 | 918 |

At row 100,000, live managed memory was 16.30/16.30/16.30 MiB and total allocation was 264.27/264.27/264.28 MiB min/median/max. The first-row retained cost is stable through all later checkpoints and is released when the reader is disposed. This distinguishes ExcelDataReader's shared-string/workbook retention from emitted-row retention.

The first XLSX repetition took 2,110 ms at row 100,000, while the later repetitions took 892 ms and 844 ms. This illustrates JIT/filesystem-cache timing noise and is why elapsed time is not an acceptance threshold.

## Early Consumption, Cancellation, and Cleanup

| Format | Early-stop rows | Early-stop bytes read | Input bytes | Cancellation point | Cancellation bytes read | Live MiB after cancellation/disposal |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CSV | 10 | 5,120 | 8,056,745 | 10,000 | 778,240 | 1.84 |
| XLSX | 10 | 862,474 | 7,313,808 | 10,000 | 1,493,258 | 1.85 |

CSV yielded ten rows after reading only a small prefix of the source. XLSX also stopped after ten worksheet rows, but its larger up-front read reflects ZIP probing and full shared-string-table loading. Both formats observed cancellation on the next move after row 10,000 and released extractor-owned resources.

## Reproducing the Experiment

The test is skipped by default so the normal suite and CI remain fast. Run it alone in Release mode from the repository root:

```powershell
dotnet build EtlTool.sln -c Release --no-restore
$env:ETL_RUN_100K_EXTRACTION_EXPERIMENT='1'
dotnet test EtlTool.IntegrationTests\EtlTool.IntegrationTests.csproj -c Release --no-build --filter "Category=Performance" --logger "console;verbosity=detailed"
```

The detailed report is written to the ignored file `TestResults/Task3A3/extraction-memory.json` and overwritten on each run. The generated XLSX is always removed in a `finally` block.

## Conclusion

The current CSV and XLSX extraction paths satisfy Task 3A.3 for the tested 100,000-row fixtures:

- enumeration is deferred and incremental;
- the count-only consumer and extractors do not retain emitted rows;
- CSV does not require an up-front full-file read;
- XLSX has a measurable, bounded shared-string initialization cost but no row-count-driven retained-memory growth after the first yield;
- cancellation, early disposal, caller stream ownership, and temporary-file cleanup remain correct at scale.

No production extractor change or corrective follow-up is required from this experiment.
