# Authorized journey attempt: peer monitor ambiguity safe abort

Observed: 2026-08-28, Asia/Shanghai

Result: SAFE_ABORT. Formal W2G G3/RC remains INCONCLUSIVE. The authorized attempt enabled the effective JourneyRuntime briefly and established a new Ready Onboard session, but the peer starter returned an unusable all-null console projection. The ambiguity rule was applied before any RIoT mutation, order creation, or vehicle movement.

## Authorization and identities

The user explicitly authorized one attempt bound to:

- OnboardHmi `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`;
- deployed ControlServer product source `6a5de0149288e3fdcedb6f9ea694259f71f9bee2`;
- JourneyRuntime enablement, creation of one RIoT order, and one empty-vehicle journey;
- immediate stop on any safety ambiguity.

The selected protected Onboard commit is authored by Kun Wang, is the direct child of the previously runtime-tested `777eff8`, and changes tests only. In a disposable clone, the WPF Release project built with zero warnings/errors and the new provider-to-business-service Ready regression passed 1/1. No protected repository was modified.

## Preconditions

The fixed privileged operator tasks were invoked successfully from the ordinary user account:

- the approved local test battery threshold was restored to 10%;
- effective-state inspection reported both base and Production JourneyRuntime layers disabled, service Running, required ports correctly owned, and threshold 10%;
- the atomic read-only preflight returned PASS with vehicle IDLE, speed zero, no order/task, matching direct and HTTPS STOPPED projections, zero safety reasons, and no prior mutation or movement;
- five current catalog items passed static route/capacity readiness; one incomplete item remained excluded by the normal admission checks.

## Attempt

The exact stop marker was removed and the fixed `8005 AGV ControlServer - Authorized Journey Orchestrator` task entered RUNNING. A second fixed effective-state inspection independently verified both JourneyRuntime configuration layers enabled, threshold 10%, service Running, and correct port ownership.

The peer starter was bound to the disposable Release output for Onboard `84b7f3f`. Its durable state file showed a new session generation 53 at `Ready`, the expected Onboard commit, simulator health Ready, and STOPPED safety projection. However, its console result contained null for every selected field despite exit code 0. Read-only inspection found the local reporting defect: the script pipes a PowerShell ordered dictionary directly through `Select-Object`, which produces null projected properties even though the serialized state file is populated.

Because the caller could not trust the direct launch result, this was treated as monitor ambiguity. The stop marker was recreated immediately and the fixed disable task was triggered. The privileged orchestrator observed the stop after 22 probe samples, finished with `STOP_REQUESTED`, and independently verified JourneyRuntime disabled. The exact Onboard and simulator PIDs from the durable peer state were then stopped and temporary ports were released.

## Final safety state

The final atomic read-only preflight returned PASS:

- both JourneyRuntime layers disabled; approved test threshold remains 10%;
- ControlServer service Running and required ports healthy;
- peer process count zero; temporary ports 1502 and 58006 free;
- vehicle IDLE, speed zero, no order/task;
- direct and HTTPS safety both STOPPED, matching, with zero reason codes;
- no RIoT mutation, no order creation, and no vehicle movement;
- the orchestrator stop marker exists.

No real journey was completed and no G3 slice can be promoted. After the safe abort, the local peer starter's result projection was corrected by converting the ordered dictionary to a `PSCustomObject` before `Select-Object`; PowerShell syntax and an offline projection against the durable READY state were validated without enabling JourneyRuntime or starting peers. Another attempt still requires fresh per-attempt authorization bound to the exact product commits.

Raw credentials, vehicle identity, candidate identities, configuration contents, and unrestricted logs are intentionally excluded.
