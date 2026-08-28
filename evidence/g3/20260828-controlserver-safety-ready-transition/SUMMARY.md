# ControlServer safety readiness transition blocker and fix

Observed: 2026-08-28, Asia/Shanghai

## Outcome

One explicitly authorized guarded Map 25 empty-vehicle attempt using protected-owner input `OnboardHmi_MVP@777eff8bdc955e6bb6fdab74ec222e0bb6748def` was stopped before order creation because the real session remained at `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` through generation 47.

Read-only simulator inspection confirmed all eight doors closed, all lock feedback active, all unlock outputs reset, and zero simulator faults. The Onboard owner change waited for its first vehicle-safety refresh and was capable of sending a higher safety revision while recovery-required. The remaining mismatch was in ControlServer production behavior: its `SafetyStateChanged` handler returned `DurableAck` alone when the updated readiness became `Ready`, while the protected-owner Fake returned both the Ack and the new `SessionReadiness`. Onboard therefore never received the transition from the previously published recovery-required state.

The owning ControlServer repository now always returns the `DurableAck` followed by the latest `SessionReadiness` after a `SafetyStateChanged`. The product fix and focused regression were pushed as `ControlServer_MVP@4347a8fb9fcb80cb9f95680a6fd8b1a0b970358b`.

## Exact inputs

- Onboard owner commit: `777eff8bdc955e6bb6fdab74ec222e0bb6748def`
- Installed ControlServer source during the failed run: `5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- ControlServer integration parent: `755067c22d146473fa8d07293a9f81f442624efe`
- ControlServer product fix: `4347a8fb9fcb80cb9f95680a6fd8b1a0b970358b`
- Protocol: `protocol-v0.1.1` / `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Simulator: protected `slots-simulator` main commit `fb5f7c593742bf98bc3957b8729a38aad5321f28`

Protected repositories were inspected and executed only from disposable copies. No protected worktree was modified.

## Guarded run observations

- Final pre-enable atomic preflight passed at catalog revision 1780 with two fully admitted static candidates.
- Vehicle was connected/enabled, Map-matched, IDLE, speed zero, with no order and a `STOPPED` projection carrying zero reason codes.
- Battery was 31% against the explicitly approved test-only 10% threshold.
- The elevated orchestrator restarted ControlServer before peers and obtained normal sanitized runtime samples.
- Simulator and exact Onboard release output started successfully and established a real TLS/NDJSON session.
- Session generation 47 remained `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`; no journey runtime row, order, or operation was created.
- The fail-closed peer timeout requested orchestrator stop, and JourneyRuntime was disabled successfully.

## Product defect and correction

Before `4347a8f`, `OnboardMessageProcessor` handled a safe `SafetyStateChanged` by persisting the new safety revision and setting in-memory readiness to `Ready`, but returned only the durable acknowledgement. It serialized `SessionReadiness` only when the decision remained recovery-required. This inverted notification condition made the client-visible readiness stale.

The corrected branch always serializes the durable acknowledgement followed by the latest readiness decision. The regression `SafeSafetyStateChangeAcknowledgesAndPublishesReadyTransition` drives the session from Ready to RecoveryRequired and back to Ready, then verifies:

- response line 1 is the correct `DurableAck` and content hash;
- response line 2 is `SessionReadiness / READY`;
- accepted safety revision advances to 3;
- readiness reason codes are empty;
- persisted and connection readiness both become Ready.

Validation on the product fix:

- focused safe-transition regression: 1 passed, 0 failed, 0 skipped;
- safe/unsafe neighboring regressions: 2 passed, 0 failed, 0 skipped;
- complete `ControlServer.Tests`: 103 passed, 0 failed, 0 skipped;
- `dotnet format ControlServer.sln --verify-no-changes --no-restore`: PASS;
- Release solution build: 0 warnings, 0 errors.

## Cleanup verification

At 2026-08-28 10:50:06 +08:00:

- JourneyRuntime was disabled and the stop marker was restored.
- ControlServer and MesIngest remained running.
- Onboard and simulator were stopped and temporary ports were released.
- Vehicle remained IDLE, speed zero, with no order.
- Direct and HTTPS safety projections agreed on `STOPPED` with zero reason codes.
- No RIoT mutation, order creation, operation, or vehicle movement occurred.

The installed ControlServer service still runs its previously deployed product until `4347a8f` is explicitly upgraded. A new guarded vehicle authorization must not be reused from this failed attempt.

Machine-readable sanitized facts are in the adjacent `result.json`. Raw configuration, credentials, candidate identities, vehicle identity, response bodies, and disposable runtime logs are intentionally excluded.
