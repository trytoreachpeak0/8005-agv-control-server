# Authorized journey attempt: batch-unlock capability config safe abort

Observed: 2026-08-28, Asia/Shanghai

Result: SAFE_ABORT. Formal W2G G3/RC remains INCONCLUSIVE. The exact Onboard session reached Ready with safe departure facts, but no candidate entered JourneyRuntime within the authorized intake window because the local peer configuration advertised `supportsBatchUnlock=false`. No RIoT mutation, order creation, or vehicle movement occurred.

## Authorization and identities

The user explicitly authorized one attempt bound to:

- OnboardHmi `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`;
- deployed ControlServer product source `6a5de0149288e3fdcedb6f9ea694259f71f9bee2`;
- JourneyRuntime enablement, one RIoT order, and one empty-vehicle journey;
- immediate stop on any ambiguity.

The protected Onboard repository remained read-only.

## Preconditions and controlled start

The fixed privileged effective-state inspection and atomic read-only preflight returned PASS. They confirmed both runtime layers disabled, threshold 10%, service and port health, vehicle IDLE, speed zero, no order/task, matching direct and HTTPS STOPPED projections with zero reasons, and six complete static route/capacity candidates. Three incomplete pickup mappings remained excluded by normal admission checks.

The exact stop marker was removed, the fixed privileged orchestrator entered RUNNING, and an independent fixed inspection verified both base and Production JourneyRuntime layers effectively enabled. The corrected peer starter then established generation 57 with `Ready / READY`, simulator Ready, and a STOPPED safety projection. The protected runtime probe independently reported the same generation, `departureSafe=true`, and JourneyRuntime enabled.

## Safe abort

The one-second monitor remained healthy: no generation growth, RecoveryRequired, UNKNOWN safety, stale protected probe, or peer PID loss occurred. Nevertheless, no JourneyRuntime row, order, or operation appeared within 20 seconds of the fresh Ready session. The monitor recreated the exact stop marker and triggered the fixed disable task. The privileged orchestrator ended with `STOP_REQUESTED`, 50 probe samples, and effective JourneyRuntime disablement. Both peers were stopped by their exact recorded PIDs and temporary ports were released.

The last sanitized protected-probe snapshot showed:

- generation 57 at `Ready / READY` with `departureSafe=true`;
- runtime count zero, order count zero, operation count zero;
- exactly six backlog rows at `ONBOARD_FACTS_NOT_READY`, matching the six complete static candidates;
- other aggregate backlog reasons remained outside the complete candidate set and are not expanded into candidate identities here.

## Root cause and local correction

Read-only source inspection identified the deterministic cause. Onboard's production-safe default is `wireToGate.supportsBatchUnlock=false`, and the local peer starter did not override it for the accepted eight-slot WIRE_TO_GATE integration profile. The Onboard session still becomes Ready because protocol recovery readiness does not require that optional capability. ControlServer's `JourneyRuntimeEngine.ReadOnboardFactsAsync`, however, deliberately rejects the capability snapshot when `supportsBatchUnlock` is false, which produces `ONBOARD_FACTS_NOT_READY` before any intake or RIoT mutation.

This is a local integration configuration defect, not an Onboard or ControlServer product-code defect. After the safe abort, the local peer starter was corrected to set `wireToGate.supportsBatchUnlock=true` explicitly. PowerShell syntax passed, the exact assignment occurs once, and an in-memory application against the selected Onboard configuration changed the safe default from false to true without starting peers or enabling JourneyRuntime. The corrected helper SHA-256 is `e31ee9a38408c54f4c6f5ff54615f0ab6a5a6418073efd6e0022bd3df407ca6a`.

## Final safety state

The final atomic preflight returned PASS:

- both JourneyRuntime layers disabled and threshold 10%;
- service and required ports healthy;
- peers zero and temporary ports free;
- stop marker present;
- vehicle IDLE, speed zero, no order/task;
- direct and HTTPS safety both STOPPED, matching, with zero reason codes;
- no RIoT mutation, no order creation, and no vehicle movement.

No real journey was completed. The local correction was not exercised under an enabled runtime because the per-attempt authorization was consumed. Another real attempt requires fresh explicit authorization bound to the exact product commits.

Raw credentials, vehicle identity, candidate identities, installed configuration contents, and unrestricted logs are excluded.

