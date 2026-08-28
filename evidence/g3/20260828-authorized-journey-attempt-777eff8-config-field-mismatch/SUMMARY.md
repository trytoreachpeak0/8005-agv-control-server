# Authorized journey attempt: Onboard safety identity field mismatch

Observed: 2026-08-28 11:03-11:06, Asia/Shanghai

Result: SAFE ABORT before order creation. JourneyRuntime was enabled under the explicit per-attempt authorization, but the new Onboard session did not become Ready. The monitor was stopped, JourneyRuntime was disabled, and no RIoT mutation, order, or vehicle movement occurred.

## Bound inputs

- Deployed ControlServer product: `ControlServer_MVP@4347a8fb9fcb80cb9f95680a6fd8b1a0b970358b`
- Protected Onboard product: `OnboardHmi_MVP@777eff8bdc955e6bb6fdab74ec222e0bb6748def`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Attempt scope: enable JourneyRuntime, allow RIoT order creation, and execute one empty-vehicle journey

No protected repository was modified.

## Preflight and guarded execution

After restoring the previously approved local test threshold that the product upgrade had reset, the elevated configuration verifier returned `PASS`: `minimumBatteryPercent=10`, `JourneyRuntime.enabled=false`, service `Running`, live HTTP 200, and an ACL-preserving rollback backup recorded outside Git.

The fresh atomic preflight at 11:03:44 returned `PASS`:

- catalog revision 1799 and four fully admitted candidates;
- vehicle connected, enabled, IDLE, on the expected map, and evidence fresh;
- battery 29% against the approved 10% test threshold;
- speed zero, lock status clear, and no order/task;
- direct and HTTPS safety projections both `STOPPED`, zero reason codes, and matching;
- JourneyRuntime disabled and no mutation performed.

The elevated orchestrator then enabled JourneyRuntime successfully and sampled it continuously. The exact Onboard and simulator disposable copies were started. A new Onboard session was established as generation 48 but remained `RecoveryRequired` with `DEPARTURE_SAFETY_NOT_READY`; the peer helper timed out rather than treating this as Ready.

The stop marker was issued immediately. The orchestrator terminated as `STOP_REQUESTED` after 51 samples, disabled JourneyRuntime successfully, and left the service Running with live HTTP 200.

## Diagnosis

The Onboard durable journal proves that both `RecoveryStateReport` and `SafetyStateChanged` were transmitted and durably acknowledged. The safety change used revision 2 but was fail-closed: `departureSafe=false`, `vehicleStopped=false`, `unknownPresent=true`, reason `VEHICLE_STATE_UNKNOWN`.

The disposable runtime configuration comparison identified the cause:

- the startup helper wrote the real vehicle key to legacy/unused property `vehicleSafety.vehicleKey`;
- Onboard `777eff8` reads `vehicleSafety.expectedVehicleKey`;
- the actual `expectedVehicleKey` therefore did not match the ControlServer journey vehicle while the unused property did.

This is a local journey-helper configuration defect, not evidence that the deployed `4347a8f` Ready-transition fix failed. ControlServer correctly kept the session in recovery when the Onboard safety provider reported UNKNOWN.

The external helper was corrected to set/add `expectedVehicleKey`; PowerShell parsing passes, it remains bound to exact Onboard commit `777eff8bdc955e6bb6fdab74ec222e0bb6748def`, and it no longer writes the unused property. The correction has not been used for another runtime-enabled attempt because the authorization recorded here was consumed when JourneyRuntime was enabled.

## Final safe state

The final atomic preflight at 11:06:21 returned `PASS`:

- JourneyRuntime disabled;
- Onboard and simulator process count zero;
- temporary listener count zero;
- vehicle IDLE, speed zero, and no order/task;
- safety `STOPPED`, zero reason codes, and matching direct/HTTPS sources;
- no RIoT mutation, order creation, or vehicle movement.

A new explicit per-attempt authorization is required before rerunning the corrected helper with JourneyRuntime enabled. Raw credentials, vehicle identity, candidate identities, response bodies, configuration values, and unrestricted logs are excluded.
