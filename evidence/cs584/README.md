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
