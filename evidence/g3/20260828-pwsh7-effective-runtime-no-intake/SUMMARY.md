# PowerShell 7 effective runtime attempt: no journey intake

Observed: 2026-08-28 11:12-11:28, Asia/Shanghai

Result: SAFE ABORT before order creation. The authorized exact product pair reached a Ready Onboard session and the ControlServer worker ran with its effective Production configuration enabled, but no journey runtime was accepted. The true candidate rejection reason was made unobservable by a confirmed backlog-fingerprint defect. No RIoT mutation, order, station operation, or vehicle movement occurred.

## Bound inputs and authorization

- Deployed ControlServer product: `ControlServer_MVP@4347a8fb9fcb80cb9f95680a6fd8b1a0b970358b`
- Protected Onboard product: `OnboardHmi_MVP@777eff8bdc955e6bb6fdab74ec222e0bb6748def`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Authorization scope: enable JourneyRuntime, permit RIoT order creation, and execute one empty-vehicle journey
- Orchestration host: PowerShell 7.6.5 (`pwsh.exe`), elevated through UAC

No protected repository was modified.

## Preflight

The first fresh atomic preflight at 11:12:05 returned `PASS`: five fully admitted static route/capacity candidates, vehicle connected/enabled/IDLE/on-map, battery 28% against the approved 10% test threshold, speed zero, no order, and matching direct/HTTPS `STOPPED` projections with zero reason codes.

The initial elevation exposed a local orchestration defect: base `appsettings.json` was enabled, but `appsettings.Production.json` still overrode JourneyRuntime to false. A read-only elevated diagnostic proved zero runtime, zero backlog, zero unresolved accepted demand, and zero orphan. No effective runtime action had occurred. The external one-shot enable/disable helpers were corrected to update, verify, back up, roll back, and finally disable both configuration layers.

After cleanup, a second atomic preflight at 11:19:02 returned `PASS`: seven fully admitted static route/capacity candidates, vehicle IDLE, battery 27%, speed zero, no order, and safety `STOPPED` with matching sources and zero reason codes.

## Effective PowerShell 7 attempt

PowerShell 7 elevation returned `PASS` with base enabled=true, Production override enabled=true, threshold 10%, service Running, and live HTTP 200. An independent elevated read-only diagnostic confirmed both effective values and observed 267 backlog rows, proving that the hosted worker was executing rather than merely reading the base configuration.

The corrected disposable Onboard configuration set `vehicleSafety.expectedVehicleKey`, not the unused legacy property. Onboard `777eff8` and the simulator started successfully. The new session reached generation 51, readiness `Ready`, and departure safety true; both peer processes remained alive during observation.

Despite this, repeated sanitized database samples showed:

- journey runtime count 0;
- order and station-operation count 0;
- unresolved accepted demand count 0;
- orphaned unresolved demand count 0;
- active vehicle dispatch lease count 0;
- backlog growing from 267 to more than 270 rows.

## Confirmed product observability defect

Every backlog row was eventually reported as `DEMAND_DECISION_FACT_CHANGED`, but that value does not identify the real eligibility failure. `JourneyRuntimeEngine.UpsertBacklogAsync` hashes the complete `AcceptedDemandSnapshot`, including volatile read metadata such as `CatalogRevision` and `AcceptedAt`. On a later poll, any fingerprint change overwrites the current computed reason with `DEMAND_DECISION_FACT_CHANGED`, even when the business decision facts did not change.

A read-only 25-second catalog comparison crossed revision 1815 to 1816 and count 272 to 270. Two demands disappeared, while all checked decision fields for the 270 common demands remained equal. This confirms that the catalog is dynamic, but it does not prove which candidate/reason blocked this intake. Because the persisted backlog reason was overwritten, the exact primary no-intake cause remains `INCONCLUSIVE` and must not be inferred from that reason code.

The runtime also accepts Onboard facts only when the handshake `CapabilitySnapshot` and `SafetyStateSnapshot` are within the configured 30-second maximum evidence age. By the end of this observation they were necessarily expired; reconnecting to refresh them would increase the session generation and was outside the guarded attempt's stop conditions. Continuing to wait could no longer safely produce a journey.

## Final safe state

The stop marker was issued. The PowerShell 7 orchestrator stopped after 237 sanitized samples and the corrected disable helper verified both base and Production JourneyRuntime overrides false. Service remained Running with live HTTP 200. Onboard and simulator were stopped by their exact process IDs and both temporary listener ports were released.

The final atomic preflight at 11:28:00 returned `PASS`:

- vehicle IDLE, speed zero, and no order/task;
- safety `STOPPED`, zero reason codes, and matching direct/HTTPS sources;
- JourneyRuntime disabled;
- no RIoT mutation, order creation, or vehicle movement.

This authorization was consumed because the effective Production runtime was enabled. A product change and a new explicit per-attempt authorization are required before another real-vehicle attempt. Raw credentials, candidate identities, vehicle identity, response bodies, configuration values, and unrestricted logs are excluded.
