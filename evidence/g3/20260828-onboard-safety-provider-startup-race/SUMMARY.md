# Onboard f16425c startup safety recovery blocker

Observed: 2026-08-28, Asia/Shanghai

## Outcome

One newly authorized guarded Map 25 empty-vehicle attempt was stopped before order creation. The exact protected-owner input `OnboardHmi_MVP@f16425cf0848fe9dd810dec241fd0d92639fc11d` no longer reproduced the earlier `SafetyStateChanged` message-ID/content collision: the session completed the handshake far enough to report `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` rather than `HANDSHAKE_INCOMPLETE`.

The session did not become `Ready` within the guarded peer-start window. The sanitized ControlServer probe observed reconnect generation growth through 46 while the same recovery reason remained active. The run was therefore stopped fail-closed; JourneyRuntime was disabled by the elevated orchestrator before any candidate acceptance, RIoT mutation, order, operation, or vehicle movement.

## Exact inputs

- Onboard owner commit: `f16425cf0848fe9dd810dec241fd0d92639fc11d`
- Installed ControlServer product source: `5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- Integration branch parent: `51466ccf861d574cff3c1947fa676d8b0c822328`
- Protocol: `protocol-v0.1.1` / `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Simulator: protected `slots-simulator` main commit `fb5f7c593742bf98bc3957b8729a38aad5321f28`

The Onboard owner commit was built and run only from a disposable clone. The protected owner worktree was not modified.

## Guarded observations

- The final pre-enable atomic preflight passed with four fully admitted static candidates.
- Vehicle gates were connected/enabled, Map-matched, IDLE, speed zero, no order, and `STOPPED` with zero safety reason codes.
- Battery was 14% against the explicitly approved test-only 10% threshold; repository and normal operating defaults remained unchanged.
- The elevated orchestrator restarted ControlServer before peers and obtained normal sanitized runtime samples.
- The simulator reached its health-ready state and Onboard established a real TLS/NDJSON session.
- The Onboard log recorded the session at generation 46 with `RecoveryRequired`; the ControlServer sanitized state identified `DEPARTURE_SAFETY_NOT_READY` and no runtime/order/operation.
- No `HANDSHAKE_INCOMPLETE` or message-ID/content-conflict state was observed in this attempt.

## Suspected protected-owner defect

Read-only source and runtime evidence strongly indicate an Onboard startup race:

1. `App.OnStartup` starts `ControlServerVehicleSafetySignalProvider` polling and immediately constructs/starts `WireToGateSessionService`.
2. `WireToGateSessionClient.ConnectAndRecoverAsync` creates the initial `SafetyStateSnapshot` from the provider's current cached value without waiting for the first fresh HTTPS observation.
3. A cold provider cache is fail-closed, so safety revision 1 can be accepted as not departure-safe even though the authoritative endpoint is already `STOPPED`.
4. Subsequent recovery remains blocked: the business service only emits `SafetyStateChanged` while session readiness is `Ready`, and reconnects continue from the already accepted safety baseline.

The owner should confirm the exact server-side revision interaction, but must not solve this by weakening the fail-closed policy or treating unknown as stopped.

## Required owner regression

- Start with an empty journal and a deliberately delayed vehicle-safety endpoint; once the endpoint returns a fresh `STOPPED` observation, the session must deterministically reach `Ready` without process restart or snapshot content conflict.
- Prove that one accepted safety snapshot revision is never reused with different content.
- Prove a cold/unknown first observation remains fail-closed while still having a defined recovery path to a later fresh safe observation.
- Retain the `f16425c` regressions: fresh journals use different durable identities even at equal timestamps, and lost-Ack replay preserves the same identity and business content.

## Cleanup verification

At 2026-08-28 10:13:38 +08:00:

- JourneyRuntime was disabled.
- ControlServer was Running under its installed service configuration.
- Onboard and simulator processes were stopped and temporary ports were released.
- Vehicle remained IDLE, speed zero, with no order.
- Direct and HTTPS safety projections agreed on `STOPPED` with zero reason codes.
- No RIoT mutation, order creation, operation, or vehicle movement occurred.

Machine-readable sanitized facts are in the adjacent `result.json`. Raw configuration, credentials, candidate identities, vehicle identity, response bodies, and disposable runtime logs are intentionally excluded.
