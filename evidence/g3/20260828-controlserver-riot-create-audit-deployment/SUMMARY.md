# ControlServer durable RIoT create-attempt audit and local deployment

Observed: 2026-08-28, Asia/Shanghai

Result: PASS. ControlServer now records durable, append-only evidence around every potential RIoT create call and the exact clean product commit was tested, packaged, pushed, and deployed locally. JourneyRuntime remained disabled throughout deployment; Onboard and the simulator were not started; no real RIoT mutation, order creation, or vehicle movement occurred.

## Authorization and scope

The user authorized modifying, testing, and deploying the ControlServer create-order audit capability on top of the current SDK integration. The authorization explicitly required JourneyRuntime to remain off and excluded starting Onboard, calling a real RIoT mutation, creating an order, or moving the vehicle.

No protected repository was modified.

## Product identity and behavior

- Product commit: `ControlServer_MVP@f07fe36ee9e8ba953a0e289d5641bf91caf513a3`
- Parent SDK-integration commit: `beb696587b58c37b962b50e002fa0651cbfe04d5`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`

The product adds an append-only `RiotDispatchAuditEvents` ledger plus nullable summaries on each order intent. It records pre-create reconciliation, atomic dispatch arm, request start, create response, and post-create reconciliation as distinct phases. ARM and START carry the same canonical request hash. A successful mutation response is recorded as acceptance only; movement confirmation still requires an independent read matching the frozen upper-id, vehicle, map, and destination.

New intents are audit version 1 with attempt count 0. Legacy rows keep null audit fields and fail closed. `CreateAttemptCount` is an EF concurrency token, preventing a stale second scope from overwriting a committed attempt. Caller cancellation and unexpected failures after START write bounded independent unknown-result evidence before propagating. Receipts exclude raw response bodies, exception messages, and credentials; business codes are sanitized at both gateway and persistence boundaries.

## Test and migration evidence

- Complete Release test suite: 156 passed, 0 failed, 0 skipped.
- Locked restore: PASS.
- Format: PASS.
- Non-incremental Release solution build: PASS, 0 warnings, 0 errors.
- EF pending-model check: no model drift after the latest migration.
- Fresh SQLite migration smoke: all 10 migrations applied; legacy audit fields remained nullable; the per-leg sequence index was unique.
- High-risk mutation audit: 18/18 behavior-changing mutations killed; one additional mutation was equivalent under the nullable-count invariant.

Every create path exercised by tests used an in-memory fake or local HTTP handler; tests did not contact the real RIoT system.

## Exact package and deployment

A fresh detached clone at the exact product commit produced a self-contained `win-x64` package. Independent validation confirmed 382 unique declared payload files, zero missing files, duplicate or escaping paths, length/hash mismatches, or undeclared payloads. Package manifest SHA-256:

`18f366b037c92964ef89b3e8d4b2e6d9d6afdbc6f27e98f99c4b75bcb31b0ff0`

The rollback-capable updater completed with run id `20260828T085940Z`. Its strict sanitized result bound the exact source commit and manifest, service Running, protocol identity, authenticated read-only safety `STOPPED` with zero reasons, JourneyRuntime false, and explicit false values for mutation, order creation, and vehicle movement. Diagnostic lifecycle markers were present in the required order with no failure or rollback marker.

After deployment, the fixed administrator task restored the already-approved 10% test threshold. Elevated read-only installation verification then confirmed:

- 381 unchanged payload files matched byte-for-byte;
- the only package-file deviation was the approved base threshold change from 30 to 10;
- the installed file set had no unexpected or missing files;
- both base and Production JourneyRuntime settings were false.

## Final safety state

- Windows service: Running / Automatic / LocalSystem.
- Ports 58005 and 58007: loopback-only and owned by the service.
- `/health/live`: `live`; every `/version` protocol-candidate field matched the package.
- JourneyRuntime: false in both configuration layers.
- Test battery threshold: 10%.
- Stop marker: present; journey orchestrator: Ready, not running.
- Onboard/simulator processes: 0; temporary peer listeners on 1502/58006: 0.
- Real RIoT mutation: false; order created: false; vehicle moved: false.

This deployment does not authorize a real journey. Raw credentials, configuration values, vehicle identity, response bodies, backup locations, and unrestricted diagnostic logs are intentionally excluded. Machine-readable sanitized facts are in the adjacent `result.json`.
