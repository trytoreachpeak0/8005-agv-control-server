# Zero-motion peer readiness drill for Onboard 84b7f3f

Observed: 2026-08-28, Asia/Shanghai

Result: PASS_WITH_OPERATIONAL_CONSTRAINT. The corrected local peer starter produced a complete non-null result, established fresh Ready sessions for the exact protected Onboard commit, and exposed the expected safe facts without ever enabling JourneyRuntime. The drill also clarified that the existing public session endpoint cannot be used to monitor `departureSafe` or the runtime's actual payload-evidence age.

## Scope and safety boundary

This was a zero-motion drill, not a real-journey attempt. Throughout the drill:

- both base and Production JourneyRuntime layers remained disabled;
- the orchestrator stop marker remained present;
- the privileged real-journey orchestrator task remained idle;
- no RIoT mutation, order creation, or vehicle movement was authorized or performed.

The exact peer identities were Onboard `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5` and the existing simulator `main@fb5f7c593742bf98bc3957b8729a38aad5321f28`.

## Preconditions

The fixed privileged effective-state inspection and atomic read-only preflight both passed. They confirmed threshold 10%, service and port health, vehicle IDLE, speed zero, no order/task, matching direct and HTTPS STOPPED projections with zero reason codes, JourneyRuntime disabled, and six currently complete static route/capacity candidates.

## Peer starter validation

The local starter was corrected after the previous safe abort by converting its ordered result dictionary to a `PSCustomObject` before `Select-Object`. It was further tightened to require both `readiness=Ready` and `reasonCode=READY`, and to include the reason code in its durable and console results. The resulting script SHA-256 is `5f2722c37aba0784e51ee1dd760bd47b23860befdf9be6074fdd7baac4357503`.

The corrected starter returned complete values rather than nulls. Its final drill session reached generation 56 with `Ready / READY`, simulator health Ready, STOPPED safety projection, and JourneyRuntime false. During the sampled observation window, generation, peer PIDs, temporary port ownership, readiness, reason code, and HTTPS STOPPED/zero-reason safety remained stable.

Read-only inspection of the disposable Onboard journal confirmed that the latest acknowledged `SafetyStateChanged` carried `departureSafe=true`, `vehicleStopped=true`, and `unknownPresent=false`. Raw identity and wire payload data are excluded.

## Observability finding

Two initial monitor checks were deliberately fail-closed but used the wrong source:

- `/api/runtime/sessions` does not expose `departureSafe`; treating the absent property as a Boolean produced a false unsafe result.
- the same endpoint's session-row `updatedAt` is the recovery-decision timestamp, not the evidence timestamp used by `JourneyRuntimeEngine`.

The product runtime evaluates the `observedAt` fields in the latest `CapabilitySnapshot` and `SafetyStateSnapshot`, with the configured 30-second maximum evidence age. An unchanged safe state does not refresh those initial handshake facts. Because peer startup consumes part of that window, a zero-motion drill must not wait 15-30 seconds after the starter returns and then claim that the actual runtime evidence is still fresh.

For the next authorized real attempt, retain the existing safe order: enable and independently verify the effective runtime first, start the peers, require a new Ready/READY generation, then let the already-running worker perform immediate intake while the protected elevated runtime probe monitors actual session/runtime facts every second. Any reconnect growth, RecoveryRequired, stale facts, missing probe status, or other ambiguity still requires an immediate stop.

## Final state

Both drill peer sets were stopped by their exact recorded PIDs. The final atomic preflight returned PASS: JourneyRuntime disabled, peer count zero, temporary ports free, stop marker present, vehicle IDLE, speed zero, no order/task, matching STOPPED safety with zero reason codes, no RIoT mutation, no order creation, and no vehicle movement.

This drill does not change formal G3/RC status and does not authorize a later real journey. A fresh per-attempt authorization is still required.

