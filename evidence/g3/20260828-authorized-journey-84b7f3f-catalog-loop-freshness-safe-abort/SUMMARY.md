# Authorized journey attempt: catalog-loop freshness safe abort

Observed: 2026-08-28, Asia/Shanghai

Result: SAFE_ABORT. Formal W2G G3/RC remains INCONCLUSIVE. The corrected batch-unlock configuration was active and the exact Onboard session was Ready and departure-safe, but the ControlServer discovery iteration did not evaluate the complete candidates until their 30-second Onboard evidence window had expired. No RIoT mutation, order creation, or vehicle movement occurred.

## Authorization and identities

The user explicitly authorized one attempt bound to:

- OnboardHmi `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`;
- deployed ControlServer product source `6a5de0149288e3fdcedb6f9ea694259f71f9bee2`;
- the corrected local `supportsBatchUnlock=true` integration configuration;
- JourneyRuntime enablement, one RIoT order, and one empty-vehicle journey;
- immediate stop on any ambiguity.

The protected Onboard repository remained read-only.

## Preconditions and controlled start

The fixed privileged effective-state inspection and atomic read-only preflight returned PASS. They confirmed both runtime layers disabled, threshold 10%, service and port health, vehicle IDLE, speed zero, no order/task, matching direct and HTTPS STOPPED projections with zero reasons, and eight complete static route/capacity candidates. Three incomplete pickup mappings remained excluded.

The peer helper passed syntax and exact-commit binding checks and contained exactly one explicit `supportsBatchUnlock=true` assignment. The fixed privileged orchestrator entered RUNNING, and an independent fixed inspection verified both runtime layers effectively enabled. The peer starter established generation 59 at `Ready / READY`; the protected probe independently confirmed `departureSafe=true`. The generated runtime configuration had batch unlock true, the exact Onboard commit, and the same hash recorded by the peer starter.

## Safe abort and timing evidence

The one-second monitor observed no reconnect, RecoveryRequired, UNKNOWN safety, protected-probe loss, or peer PID loss. Nevertheless, no JourneyRuntime row, order, or operation appeared within 20 seconds of Ready, so the monitor recreated the stop marker and triggered the fixed disable task.

The last sanitized protected-probe state showed:

- session generation 59 at `Ready / READY`, `departureSafe=true`;
- runtime absent, zero orders, zero operations;
- nine backlog entries at `ONBOARD_FACTS_NOT_READY`;
- session Ready timestamp 13:23:06;
- latest `ONBOARD_FACTS_NOT_READY` observation 13:23:51;
- JourneyRuntime had been enabled at 13:22:29.

Thus the relevant backlog evaluation arrived about 45 seconds after the fresh Ready decision and about 82 seconds after runtime startup. The configured Onboard evidence maximum age is 30 seconds. Explicit batch-unlock configuration removed the prior capability flag blocker, but the next eligibility evaluation arrived after the evidence window.

## ControlServer safety finding

Read-only source inspection found a safety-sensitive sequencing gap in `JourneyRuntimeEngine.DiscoverAndAcceptAsync`:

- it captures `now`, reads Onboard facts, and reads RIoT vehicle facts before processing the full catalog;
- it then performs the complete per-candidate route, capacity, station policy, box-count, backlog, and selection loop;
- it may finally call `AcceptAndDispatchToPickupAsync` without rereading dynamic Onboard/RIoT facts or advancing `now` immediately before the external mutation.

The field timing shows that a discovery iteration can exceed the 30-second evidence window. Starting peers before runtime would make the initial facts available earlier, but with the current structure it could also allow a long iteration to dispatch using facts that were fresh only at iteration start. Therefore reversing startup order is not an acceptable workaround until a final dynamic-fact freshness gate exists.

Before another real attempt, the ControlServer owner should add a just-before-intake dynamic reread and fail-closed validation that binds the same session generation and requires current Capability/Safety payload timestamps, departure safety, stopped/locked/reset slot state, and current RIoT vehicle facts. A regression should advance time beyond `MaximumEvidenceAge` during candidate processing and prove that no RIoT mutation occurs. Reducing the long catalog iteration is still valuable, but performance alone must not replace the final safety gate.

No product code was changed in this attempt because the authorization covered the real journey, not a new product modification/test/deployment cycle.

## Final safety state

The monitor recreated the exact stop marker; the privileged orchestrator ended `STOP_REQUESTED`, verified JourneyRuntime disabled, and recorded 35 probe samples. Both peers were stopped by exact PID and temporary ports were released. The final atomic preflight returned PASS: vehicle IDLE, speed zero, no order/task, direct and HTTPS STOPPED with matching sources and zero reasons, JourneyRuntime disabled, no RIoT mutation, no order, and no movement.

This attempt is consumed. A product fix, focused and full validation, deployment, and then fresh per-attempt authorization are required before another real journey.

Raw credentials, vehicle identity, candidate identities, installed configuration contents, and unrestricted logs are excluded.

