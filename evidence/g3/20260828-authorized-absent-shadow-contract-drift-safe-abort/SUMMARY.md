# Authorized absent-observation shadow: contract-drift safe abort

Result: **SAFE_ABORT**. The isolated shadow session became `Ready`, but the
ControlServer accepted no demand and created no journey, intent, order, or
station operation. No RIoT mutation was forwarded and the vehicle did not move.

The exact cause was an intentionally fail-closed MesIngest contract check. The
tested ControlServer package required `2026.08.new-mes-ingest.v2.3` / schema 29,
while the live read-only contract endpoint returned the already-frozen
`2026.08.new-mes-ingest.v2.4` / schema 29 identity. Four capability versions
also changed with that cutover: `CURRENT_INGEST_ATTENTION/2.1`,
`DEMAND_SERIES/2.1`, `ERROR_SEARCH/2.2`, and
`EXTERNALLY_READABLE_DEMAND_CATALOG/2.1`.

The 150-second timeout was therefore not causal. Across 81 iterations, the
MesIngest contract read returned HTTP 200 and was rejected before catalog
interpretation. The shadow database retained only one ready session: accepted
demands, backlog entries, journey runtimes, order intents, dispatch audits,
experimental authorizations, leases, and station operations all remained zero.
The read-only egress path observed GET requests only; there were no mutation
methods.

The consumer now pins the exact v2.4 identity and capability inventory without
relaxing compatibility. The shadow runner also checks that identity before any
runtime or peer starts, captures proxy counters on failure, and stores any
future private permit under a restricted ACL. Validation passed:

- focused `HttpMesIngestCatalogTests`: 2 passed, 0 failed, 0 skipped;
- full `ControlServer.Tests`: 218 passed, 0 failed, 0 skipped;
- PowerShell parsing, proxy self-test, Python aggregate diagnosis, and
  `git diff --check`: pass;
- synthetic permit-state validation accepted the exact closed pre-create state
  and rejected an added `CREATE_DISPATCH` audit.

No replacement package was deployed and no second shadow session was started
as part of this remediation. The authorization used for the effective shadow
session must not be reused.
