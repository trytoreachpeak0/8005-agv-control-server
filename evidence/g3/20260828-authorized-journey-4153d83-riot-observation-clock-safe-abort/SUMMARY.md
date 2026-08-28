# Authorized journey attempt: RIoT observation clock-order safe abort

Observed: 2026-08-28, Asia/Shanghai

Result: SAFE_ABORT. Formal W2G G3/RC remains INCONCLUSIVE. The exact authorized Onboard session reached `Ready / READY` and stayed departure-safe, and the corrected independent HTTPS monitor stayed STOPPED. Nevertheless, all 16 statically complete candidates were rejected as `RIOT_VEHICLE_FACT_STALE` before intake because the production discovery loop compares a newly read observation against a timestamp captured before that read. No RIoT mutation, order creation, or vehicle movement occurred.

## Authorization and identities

The user explicitly authorized one attempt bound to:

- OnboardHmi `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`;
- deployed ControlServer product source `4153d8369262a5a574589258b6c32651bc043c79`;
- local `supportsBatchUnlock=true` integration configuration;
- JourneyRuntime enablement, one RIoT order, and one empty-vehicle journey;
- read-only HTTPS monitoring with `CONTROL_SERVER_ONBOARD_CREDENTIAL`;
- immediate stop on any safety ambiguity.

The protected Onboard and simulator repositories remained read-only.

## Preconditions and controlled start

The fresh fixed privileged inspection returned PASS: both JourneyRuntime layers false, threshold 10%, service Running, ports 58005/58007 owned only by the service, HTTPS live healthy, and stop marker present.

The atomic read-only preflight returned PASS: vehicle IDLE, speed zero, no order/task, battery 49%, direct and HTTPS safety both STOPPED with zero reasons, and 16 static route/capacity-ready candidates. A separate corrected-credential safety request returned HTTP 200, STOPPED, and zero reasons before enablement.

The one-use monitor script passed syntax and static credential checks; SHA-256 was `7f55a1bde64cd7aeb60b2dd978cb917e885b9bf827e6754059b5d9fbc6eb55ba`. The fixed orchestrator entered RUNNING, and an independent fixed inspection verified both runtime layers effectively true at threshold 10%.

The exact peer helper had SHA-256 `e31ee9a38408c54f4c6f5ff54615f0ab6a5a6418073efd6e0022bd3df407ca6a`. It started Onboard `84b7f3f`, simulator `fb5f7c5`, and returned configuration SHA-256 `e43ce30481ede92bf60e8dd5b796c2acd571d378991aeb917e81b8dfe1048fb8`, simulator `READY`, and generation 63 at `Ready / READY`.

## Monitored safe abort

The dedicated monitor took 23 successful samples. Every sample retained the same generation, fresh protected-probe delivery, live exact peer PIDs, session `Ready / READY`, `departureSafe=true`, and HTTPS motion STOPPED. It observed no runtime, no order, and no operation. At the conservative evidence deadline it wrote the stop marker and triggered the fixed disable task instead of waiting for stale Onboard evidence or reconnecting.

The orchestrator finished `STOP_REQUESTED` after 28 protected-probe samples, with no terminal stage, no failure, and `journeyRuntimeDisabled=true`.

The last protected probe showed 16 backlog rows at `RIOT_VEHICLE_FACT_STALE`, latest observed at 14:01:04, while generation 63 was still `Ready / READY` and departure-safe. The ordering of validations means these candidates had already passed the earlier Onboard-ready/departure-safe, RIoT connected/enabled, vehicle identity, IDLE, and current-map checks.

## ControlServer source finding

Read-only inspection of deployed product source identified the blocking clock-order defect:

- `JourneyRuntimeEngine.DiscoverAndAcceptAsync` captures `now` at the beginning of the iteration, before the asynchronous catalog, Onboard, and RIoT vehicle reads (`JourneyRuntimeEngine.cs`, lines 116–130).
- `HttpRiotMovementGateway.ReadVehicleAsync` stamps a successful observation with `timeProvider.GetUtcNow()` only after the HTTP response is received and parsed (`HttpRiotMovementGateway.cs`, lines 136–172).
- the initial candidate dynamic gate rejects the observation when `vehicle.ObservedAt > now` (`JourneyRuntimeEngine.cs`, lines 497–510).

On a real network call, the freshly generated observation timestamp is later than the iteration-start timestamp. It is therefore misclassified as future/stale, the candidate never remains `ELIGIBLE`, and the engine returns before intake. The current fixed-clock tests do not expose this because their clock does not advance during the fake vehicle read.

The corrected `/api/onboard/v1/vehicle-safety` monitor is a separate Round-41 projection using different RIoT endpoints and predicates. Its HTTP 200/STOPPED result does not execute or prove the JourneyRuntime vehicle observation time gate, so the two observations are consistent.

The appropriate owner fix is to evaluate freshness against a timestamp captured after the observation read, as the later final dynamic gate already does, and add a regression whose clock advances during the vehicle read. No product code, test, package, or deployment was changed under this journey-only authorization.

## Final safety state

The final fixed inspection and atomic read-only preflight both returned PASS:

- both JourneyRuntime layers false, threshold 10%, stop marker present;
- service Running and live healthy;
- vehicle IDLE, speed zero, no order/task;
- direct and HTTPS safety STOPPED with matching source and zero reasons;
- no peer process and no temporary listener;
- no RIoT mutation, no order creation, and no vehicle movement.

This authorization was consumed when effective JourneyRuntime was enabled. A focused product fix, regression validation, deployment, and then a fresh exact real-journey authorization are required before another attempt.

Raw credentials, vehicle identity, candidate identities, installed configuration contents, and unrestricted logs are excluded.
