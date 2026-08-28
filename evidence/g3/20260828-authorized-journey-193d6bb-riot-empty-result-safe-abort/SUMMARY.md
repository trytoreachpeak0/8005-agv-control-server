# Authorized journey attempt: RIoT empty reconciliation result safe abort

Observed: 2026-08-28, Asia/Shanghai

Result: SAFE_ABORT after durable journey intake but before any confirmed RIoT order or vehicle movement. Current safety classification is `SAFE_NOW / ORPHAN_NOT_YET_EXCLUDED`; formal W2G G3/RC remains INCONCLUSIVE. The deployed clock-order fix removed the previous intake blocker: generation 65 accepted one Demand and persisted one frozen TO_PICKUP intent. RIoT upper-id reconciliation then remained `RESULT_UNKNOWN` because the live endpoint returned an HTTP-success envelope without a `result` field. The explicit stop-on-ambiguity rule was applied; no second intent or blind create retry was allowed.

## Authorization and identities

The user explicitly authorized one attempt bound to:

- OnboardHmi `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`;
- deployed ControlServer product source `193d6bbb1430b807b4db471975707cb6ce8c36fd`;
- local `supportsBatchUnlock=true` integration configuration;
- JourneyRuntime enablement, at most one RIoT order, and one empty-vehicle journey;
- read-only HTTPS safety monitoring with `CONTROL_SERVER_ONBOARD_CREDENTIAL`;
- immediate stop on any safety ambiguity.

The protected Onboard and simulator repositories remained read-only.

## Preconditions and controlled start

The successful exact-package deployment result bound the installed product to `193d6bbb1430b807b4db471975707cb6ce8c36fd` and manifest `2a10eddda8019afb7d228df778d0fdcc8fb3595c07ce8c9083a4b9c11ea8c16b`.

Fresh fixed inspection and atomic preflight returned PASS: both runtime layers false, threshold 10%, stop marker present, service Running/live, vehicle IDLE, speed zero, no order/task, battery 46%, direct and HTTPS safety STOPPED with zero reasons, and six static route/capacity-ready candidates. A separate request using the corrected named credential also returned HTTP 200/STOPPED with zero reasons.

The orchestrator entered RUNNING, and fixed inspection proved both runtime layers effectively true. The exact peer helper started Onboard `84b7f3f` and simulator `fb5f7c5`; configuration SHA-256 was `cae74ba1cec1d31d30005a50703dc4a51b540a3df722adebfae1b9daf341f0f9`. Generation 65 reached `Ready / READY` and remained departure-safe.

## Intake progress and ambiguity

The new ControlServer fix succeeded at its intended boundary. Before the third monitor sample, the protected probe showed:

- AcceptedDemand persisted with status `Accepted`;
- JourneyRuntime at `AwaitingPickupArrival`;
- expected basket count 2 and target slot count 2;
- exactly one persisted TO_PICKUP OrderIntent;
- no station operation or completion state.

The monitor's `orderCount=1` describes that persisted OrderIntent; it is not proof of a confirmed RIoT order.

The intent remained `RESULT_UNKNOWN`, `orderConfirmed=false`, and runtime block reason `PICKUP_ResultUnknown` for the rest of the run. The dedicated monitor recorded 138 safe samples with the same generation and at most one intent. HTTPS motion remained STOPPED. No second intent, changed upper-id, or blind mutation retry occurred.

Because the RIoT result could not be reconciled, the stop marker was written and the fixed disable task was triggered. The orchestrator ended `STOP_REQUESTED`, reported no failure, and confirmed JourneyRuntime disabled. Both peers were stopped by their exact PIDs and temporary listeners were released.

## Read-only RIoT response finding

After stop, a sanitized read-only `GET /api/order/v1/orderRecord/detailByUpperId/{frozen-id}` returned:

- HTTP 200;
- business code string `"0"`;
- JSON length 55;
- top-level keys `code`, `message`, `msgDetail`, and `tid`;
- no `result` field.

No frozen id, order id, vehicle id, credential, response message, or raw body was printed or retained.

`HttpRiotMovementGateway.ReconcileByUpperIdAsync` treats only HTTP 404 as confirmed absence. An HTTP-success envelope is passed to `ToObservation`, which requires a non-null result with exact order identity and state; an absent result therefore correctly fails closed as Unknown under the current contract. `MovementDispatchService` permits a POST only when a PENDING_RECONCILIATION intent receives confirmed NotFound. The existing regression `OnlyHttp404IsConfirmedNotFound` deliberately enforces this boundary. This is the most credible reason the run stopped before creation: the live RIoT "no record" shape is incompatible with the product's confirmed-absence contract.

The persisted state does not record enough detail to prove whether the create POST was reached in this historical iteration; `RESULT_UNKNOWN` can represent either an unknown pre-create reconciliation or an unknown mutation response. The current live response and absence of any RIoT order/task strongly indicate the flow stopped at the pre-create reconciliation boundary, but this evidence does not promote that inference to a confirmed mutation audit. Therefore this report does not claim that a RIoT order was created, nor does it claim a provable zero-POST history.

No outbound mutation audit exists in the current persistence model or service events, so the high-confidence pre-create inference is not a forensic proof. Before changing the adapter or recovering this intent, the RIoT owner/operator must confirm the contract meaning of HTTP 200/code `0` with no result and provide a durable audit for the frozen upper-id. Treating the envelope as NotFound without that confirmation could authorize a duplicate movement.

If confirmed, the ControlServer change must be scoped narrowly: only a successful reconciliation GET with business code `0` and missing/null result may become NotFound; a create response with missing result must remain Unknown and must never be blindly retried. Regressions must cover both cases. The existing `RESULT_UNKNOWN` intent then needs an explicit controlled recovery decision backed by the RIoT audit; it must not simply be reset or recreated.

## Final safety state

The immediate final inspection/preflight, another delayed preflight at 14:30:27, and two later HTTPS safety checks through 14:32:48 all reported the same safe live state. The last check was about 8 minutes 38 seconds after intent creation:

- both JourneyRuntime layers false, threshold 10%, stop marker present;
- service Running/live;
- vehicle IDLE, speed zero, no live RIoT order/task;
- direct and HTTPS safety STOPPED with matching source and zero reasons;
- no peer process and no temporary listener;
- no vehicle movement observed.

These finite checks show the vehicle is safe now, but they cannot mathematically exclude a remote order appearing later if RIoT has an unbounded asynchronous queue. A definitive orphan-order closure therefore requires either a RIoT service-side audit or continued read-only observation for an official maximum request-processing and queue-settlement bound. Until then the result is `SAFE_NOW / ORPHAN_NOT_YET_EXCLUDED`, not PASS.

The AcceptedDemand, JourneyRuntime, dispatch lease, and unknown OrderIntent remain durably fail-closed; this report does not delete or rewrite them. Any future enablement would encounter that unresolved state and requires an explicit recovery decision, not another ordinary journey authorization.

This one-attempt authorization was consumed. Raw credentials, frozen ids, vehicle identity, candidate identity, installed configuration contents, and unrestricted logs are excluded.
