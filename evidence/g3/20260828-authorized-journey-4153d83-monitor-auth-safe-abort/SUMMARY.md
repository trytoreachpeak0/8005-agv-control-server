# Authorized journey attempt: independent safety monitor authentication safe abort

Observed: 2026-08-28, Asia/Shanghai

Result: SAFE_ABORT. Formal W2G G3/RC remains INCONCLUSIVE. The exact authorized peers reached a fresh departure-safe session, but the independent HTTPS safety monitor returned 401 because its ad hoc request used the wrong named credential. The explicit stop-on-ambiguity rule was applied immediately. No RIoT mutation, order creation, or vehicle movement occurred.

## Authorization and identities

The user explicitly authorized one attempt bound to:

- OnboardHmi `OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`;
- deployed ControlServer product source `4153d8369262a5a574589258b6c32651bc043c79`;
- local `supportsBatchUnlock=true` integration configuration;
- JourneyRuntime enablement, one RIoT order, and one empty-vehicle journey;
- immediate stop on any safety ambiguity.

The protected Onboard repository remained read-only. The deployed ControlServer identity was verified from the successful exact-package upgrade result, whose manifest SHA-256 is `7b966c865e0c2e8d7d4305c68838daad1ba84141a3fd4c5d1df68f2ede61dcc0`.

## Preconditions and controlled start

The fresh fixed privileged inspection returned PASS: both JourneyRuntime configuration layers were false, the approved test threshold was 10%, the service was Running, ports 58005/58007 were owned only by the service, HTTPS live was healthy, and the stop marker existed.

The atomic read-only preflight returned PASS: vehicle connected and IDLE, speed zero, no order/task, battery 50%, direct and HTTPS safety both STOPPED with zero reason codes, and 15 static route/capacity-ready candidates. It recorded no RIoT mutation, order, or movement.

The peer helper passed syntax and binding checks, contained exactly one `supportsBatchUnlock=true` assignment, and had SHA-256 `e31ee9a38408c54f4c6f5ff54615f0ab6a5a6418073efd6e0022bd3df407ca6a`. After the stop marker was removed, the fixed orchestrator entered a fresh RUNNING state. A second fixed inspection verified both runtime layers effectively true with threshold 10%.

The helper then started the exact Onboard and simulator binaries. It returned simulator `READY`, session generation 61 at `Ready / READY`, safety projection STOPPED, and configuration SHA-256 `349b0e5d26b159c142d2aa16114fb855bd3b6792c922ef314daab8c74b23a5df`. The protected probe was fresh and independently reported generation 61, `Ready / READY`, `departureSafe=true`, runtime absent, zero orders, and zero operations.

## Immediate safe abort

The additional HTTPS monitor attempted `GET /api/onboard/v1/vehicle-safety` with the machine-scope RIoT Call API Key and received HTTP 401. This prevented the required independent safety confirmation. The stop marker was recreated immediately, the fixed disable task was triggered, and both exact peer PIDs were stopped after process-name verification. No retry was attempted.

The orchestrator finished `STOP_REQUESTED` after 33 protected-probe samples, with no terminal journey stage, no failure, and `journeyRuntimeDisabled=true`.

## Offline diagnosis

Read-only source inspection confirmed that `/api/onboard/v1/vehicle-safety` authenticates against the credential named by `OnboardSafetyProjectionOptions`, while the existing peer helper correctly uses machine-scope `CONTROL_SERVER_ONBOARD_CREDENTIAL`. The failed ad hoc monitor instead supplied `CONTROL_SERVER_RIOT_CALL_API_KEY`. Therefore the 401 was an operator-monitor credential-selection error, not evidence that the vehicle safety projection had become unsafe.

After JourneyRuntime was disabled and peers were gone, a read-only request using the correct named Onboard credential returned HTTP 200, STOPPED, source `RIOT_BEHAVIOR_LAB_R41`, and zero reason codes. Credential values were never printed, hashed, logged, or committed.

This correction does not revive the consumed authorization: effective JourneyRuntime had already been enabled. Any further real attempt requires a fresh explicit authorization.

## Final safety state

The final fixed inspection and atomic read-only preflight both returned PASS:

- both JourneyRuntime layers false and stop marker present;
- service Running, live healthy, required service ports healthy;
- vehicle IDLE, speed zero, no order/task;
- direct and HTTPS safety STOPPED with matching source and zero reason codes;
- no Onboard/simulator process and no temporary listener;
- no RIoT mutation, no order creation, and no vehicle movement.

Raw credentials, vehicle identity, candidate identities, installed configuration contents, and unrestricted logs are excluded.
