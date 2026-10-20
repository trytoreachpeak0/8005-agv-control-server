# control-server#584 evidence

Laptop (7945HX, 32 threads, 63 GB), 2026-10-20. Full logs and TRX files stayed in `C:/work/8005-scratch/cs584/evidence/`.

- `red-test-commit/test-summary.txt` — test commit `be9fbebf` (cache disk I/O switched off, validator and gate script
  unchanged): `SchemaCompilationCacheTests`, `SchemaConformanceToolTests`, `G2GateScriptReportLocationTests`,
  `IntegrationSliceTraitArchitectureTests`: 11 failed, 27 passed.
- `green-impl-commit/test-summary.txt` — implementation `df325807` + format fix `de2ab3cf`: those four classes and every
  `*ArchitectureTests`, 114 passed.
- `mutations/` — `SUMMARY.txt` and the diff of each mutation of `CompiledSchemaCache.cs`, run against
  `SchemaCompilationCacheTests`.
- `comparison/` — the same three slices (FP-IS-03, FP-IS-04, FP-IS-13) four ways, run in the order 1, 2, 4, 3 inside a
  laptop slot granted by the coordinator; `*.heavy-status-before.txt` is `Get-HeavyLocalStatus.ps1` just before each
  round (one other ticket's synthetic L2 was running throughout). Per slice: `gate-result.json` and
  `schema-coverage.json`.
  1. `before-base-per-slice` — base `1f31a4a4`, old script, one call per slice (each call builds twice, no cache).
  2. `after-cold-one-call` — `de2ab3cf`, new script, one call for all three, schema cache emptied first.
  4. `same-commit-old-script-per-slice` — `de2ab3cf`, the base script run one call per slice (cache warm).
  3. `after-warm-one-call` — `de2ab3cf`, new script, one call, cache warm.

## Per slice (single sample each, another ticket's synthetic L2 running concurrently)

Seconds. "test" is a slice's `startedAt`→`finishedAt` in `gate-result.json` (the `dotnet test` run, validator included);
"compile" is `schemaCompilationMilliseconds` from `schema-coverage.json`, summed over the validator's processes (effort,
not wall time); "non-test" is the round's wall time minus its three test runs (builds, listings, script overhead).

| Round | FP-IS-03 test / compile / cache | FP-IS-04 test / compile / cache | FP-IS-13 test / compile / cache | non-test | total |
|---|---|---|---|---|---|
| 1 base, old script per slice | 59.2 / 63.6 / none | 33.4 / 31.4 / none | 50.1 / 34.6 / none | 31.4 | 174.1 |
| 2 new script, one call, cold | 50.6 / 53.0 / 0 hit 15 miss | 20.1 / 0.3 / 11 hit | 37.4 / 8.6 / 11 hit 3 miss | 16.9 | 125.0 |
| 4 old script per slice, warm | 37.8 / 0.4 / 15 hit | 25.6 / 0.5 / 11 hit | 50.7 / 0.5 / 14 hit | 29.3 | 143.4 |
| 3 new script, one call, warm | 35.5 / 0.5 / 15 hit | 23.0 / 0.5 / 11 hit | 50.5 / 0.6 / 14 hit | 16.4 | 125.4 |

- FP-IS-13 in round 2 (37.4 s) is an outlier: about 50 s in the other three rounds. That outlier is why the cold and
  warm totals come out 0.4 s apart; on FP-IS-03, the slice that pays the cold compilation, cold is 15.1 s slower than
  warm.
- Building once is the steadiest saving: non-test time 31.4 and 29.3 s with the old script, 16.9 and 16.4 s with the
  new one, about 13 s for three slices.
- The totals (174.1 → 125.0 / 125.4 s) are one sample each under concurrent load, not a benchmark.