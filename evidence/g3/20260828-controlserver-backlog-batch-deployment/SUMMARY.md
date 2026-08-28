# ControlServer backlog admission fix and local deployment

Observed: 2026-08-28, Asia/Shanghai

Result: PASS. The journey backlog fingerprint and per-candidate persistence defects were reproduced, fixed, tested, pushed, packaged from the exact clean commit, and deployed locally while JourneyRuntime remained disabled.

## Product fix

Published product commit: `ControlServer_MVP@6a5de0149288e3fdcedb6f9ea694259f71f9bee2`

The previous implementation serialized the complete `AcceptedDemandSnapshot` into `JourneyBacklogRow.DecisionFingerprint`. That included volatile read metadata (`CatalogRevision` and `AcceptedAt`), so a later catalog read overwrote the current eligibility reason with `DEMAND_DECISION_FACT_CHANGED`. It also queried and saved one backlog row per candidate. The authorized field run observed more than 250 candidates and 256 `SaveChanges` calls in the focused reproduction, consuming the 30-second Onboard handshake-evidence window before a subsequent worker iteration could intake a journey.

The fix:

- fingerprints only the same stable decision facts used by final demand intake, plus the history epoch;
- excludes volatile catalog revision and read timestamp metadata;
- updates the fingerprint baseline when a real decision fact changes, so the transition is reported once and later observations recover the current eligibility reason;
- preloads backlog rows into a dictionary and performs a bounded batch save instead of per-candidate query/save amplification.

## Test-first evidence

Before the product change, the two new focused regressions failed as intended:

- `VolatileCatalogReadMetadataDoesNotOverwriteCurrentBacklogReason` expected `BATTERY_FACT_UNKNOWN` but observed `DEMAND_DECISION_FACT_CHANGED`;
- `LargeCatalogBatchesBacklogPersistenceBeforeAcceptingEligibleJourney` observed 256 saves, outside its permitted 1-10 bound.

After the fix:

- focused regressions: 2/2 PASS, 0 skip;
- complete `JourneyRuntimeWorkerTests`: 30/30 PASS, 0 skip;
- complete `ControlServer.Tests`: 105/105 PASS, 0 skip;
- `dotnet format ControlServer.sln --verify-no-changes --no-restore`: PASS;
- Release solution build: PASS, 0 warnings, 0 errors.

The generated tests were re-read against the source. The fingerprint test captures immutable string values before subsequent EF tracking updates, and the large-catalog test asserts both the accepted eligible journey and the bounded save count.

The protected Onboard owner commit `84b7f3f66ff2f867b18121760f38e26e0bbd6fa5` was also inspected read-only. It changes tests only; its new end-to-end provider-to-business-service Ready regression passed 1/1 in a disposable copy. No protected repository was modified.

## Package and deployment

A clean detached disposable clone at exact commit `6a5de0149288e3fdcedb6f9ea694259f71f9bee2` produced a self-contained `win-x64` package:

- manifest SHA-256: `d61cb025215635df0e5d0304526dd22e05471e3a801381c57d3e77f918b7d942`;
- declared payload files: 371;
- independent pre-deploy file/hash mismatches: 0.

The existing PowerShell 7 rollback-capable updater required the installed Production JourneyRuntime override to be false, validated every manifest entry, created ACL-restricted install/data backups, atomically replaced the install, and exercised stop/start/restart, live, version, and authenticated read-only safety checks. Its sanitized result returned:

- result `PASS` and exact source commit `6a5de0149288e3fdcedb6f9ea694259f71f9bee2`;
- service `Running`;
- protocol `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`;
- authenticated safety projection `STOPPED`, HTTP 200, zero reason codes;
- JourneyRuntime disabled;
- no RIoT mutation, order creation, or vehicle movement.

## Independent post-deployment checks

- Windows service: Running / Automatic / LocalSystem.
- Ports 58005 and 58007 are both present and owned only by the current service process.
- `/health/live` and `/version` returned the expected live/protocol identities.
- The fresh 12:27:51 atomic preflight returned `PASS`: vehicle IDLE, speed zero, no order/task, direct and HTTPS safety both `STOPPED` with zero reason codes and matching sources.
- JourneyRuntime remained disabled; Onboard and simulator were not started.

The deployed fix removes the confirmed ControlServer intake amplification and reason-corruption blockers. It does not authorize a real journey. A new explicit authorization must bind the deployed `6a5de01` ControlServer commit and the selected protected Onboard commit before JourneyRuntime, RIoT order creation, or vehicle movement is attempted. Raw credentials, candidate identities, vehicle identity, backup paths, configuration contents, and unrestricted logs are excluded.
