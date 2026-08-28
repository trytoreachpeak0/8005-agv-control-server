# ControlServer final dynamic-facts gate and local deployment

Observed: 2026-08-28, Asia/Shanghai

Result: PASS. ControlServer now rereads and fail-closed validates Onboard and RIoT dynamic facts after long candidate processing and again after the final catalog refresh, before persisting an accepted journey or invoking RIoT. The fix was tested, pushed, packaged from the exact clean commit, and deployed locally while JourneyRuntime remained disabled.

## Product fix

Published product commit: `ControlServer_MVP@4153d8369262a5a574589258b6c32651bc043c79`

The field attempt proved that a discovery iteration can outlive the configured 30-second Onboard evidence window. The previous engine read `now`, Onboard facts, and RIoT vehicle facts before processing the full catalog, then could accept and dispatch without another dynamic read.

The fix adds two explicit gates:

- after candidate processing and selection, reread current Onboard and RIoT facts, require the same session generation, require every selected slot still available, and reapply all dynamic safety/freshness checks;
- after `DemandIntakeService` performs its final catalog reread, run the same gate again before writing AcceptedDemand, JourneyRuntime, or OrderIntent state.

Either failure records `FINAL_DYNAMIC_FACTS_NOT_READY` and returns before RIoT reconciliation or creation. Journey plan and pickup intent timestamps are now created from the post-loop intake time rather than the stale iteration-start time.

## Test-first evidence

The first focused regression failed before the product change by observing an AcceptedDemand after the fake clock advanced 31 seconds during candidate processing. After the fix:

- `CandidateProcessingThatExpiresDynamicFactsDoesNotAcceptOrDispatch`: PASS;
- `FinalCatalogRefreshThatExpiresDynamicFactsDoesNotAcceptOrDispatch`: PASS;
- focused new regressions: 2/2 PASS, 0 skip;
- complete `JourneyRuntimeWorkerTests`: 32/32 PASS, 0 skip;
- complete `ControlServer.Tests`: 107/107 PASS, 0 skip;
- `dotnet format ControlServer.sln --verify-no-changes --no-restore`: PASS;
- Release non-incremental solution build: PASS, 0 warnings, 0 errors.

Both regressions assert the secondary safety effects, not only a return value: AcceptedDemand, JourneyRuntime, and OrderIntent remain empty; RIoT create count remains zero; backlog reason is `FINAL_DYNAMIC_FACTS_NOT_READY`; AcceptedAt remains null. One test advances time during candidate processing and the other during the final catalog refresh.

## Package and deployment

A clean disposable clone at exact commit `4153d8369262a5a574589258b6c32651bc043c79` produced a self-contained `win-x64` package:

- declared payload files: 371;
- manifest SHA-256: `7b966c865e0c2e8d7d4305c68838daad1ba84141a3fd4c5d1df68f2ede61dcc0`;
- independent file/hash mismatches: 0.

The existing rollback-capable updater required installed Production JourneyRuntime=false, validated the complete package manifest, created restricted install/data backups, atomically replaced the install, and exercised service stop/start/restart, live, version, and authenticated read-only safety checks. The successful result was written to `controlserver-4153d83-upgrade-result-20260828134140528.json` and reported:

- result PASS and exact source commit `4153d8369262a5a574589258b6c32651bc043c79`;
- exact package manifest hash;
- service Running;
- safety projection STOPPED with zero reason codes;
- JourneyRuntime disabled;
- no RIoT mutation, no order creation, and no vehicle movement.

The deployment package restored the repository-default base threshold. The fixed privileged operator task then restored the previously approved local test threshold to 10% and independently verified both JourneyRuntime layers remain disabled.

## Independent post-deployment state

The final atomic read-only preflight returned PASS:

- vehicle IDLE, speed zero, no order/task;
- direct and HTTPS safety both STOPPED, matching, with zero reason codes;
- service and required ports healthy;
- JourneyRuntime disabled and stop marker present;
- no RIoT mutation, no order creation, and no movement;
- twelve current catalog items passed static route/capacity readiness; four incomplete pickup mappings remained excluded.

This deployment does not authorize a real journey. The next attempt must use a fresh explicit authorization bound to deployed ControlServer `4153d83` and the selected protected Onboard commit.

Raw credentials, vehicle identity, candidate identities, installed configuration contents, backup paths, and unrestricted logs are excluded.

