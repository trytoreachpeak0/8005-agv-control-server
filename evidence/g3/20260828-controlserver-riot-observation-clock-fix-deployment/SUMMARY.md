# ControlServer RIoT observation clock-order fix and deployment

Observed: 2026-08-28, Asia/Shanghai

Result: PASS. ControlServer now validates the initial RIoT vehicle observation against a current time captured after that observation is read. The regression failed on the deployed old implementation, passed after the minimal fix, and the exact clean product commit was tested, packaged, pushed, and deployed locally. JourneyRuntime remained disabled throughout deployment; no RIoT mutation, order creation, or vehicle movement occurred.

## Authorization and scope

The user authorized modifying, testing, pushing, packaging, and locally deploying the clock-order fix on `8005-agv-control-server/ControlServer_MVP`, followed by restoring the approved 10% test threshold and running read-only safety preflight. The authorization explicitly excluded modifications to OnboardHmi, slots-simulator, and the protocol repository, and did not authorize JourneyRuntime enablement, RIoT order creation, or vehicle movement.

The protected repositories were not modified.

## Product fix

Published product commit: `ControlServer_MVP@193d6bbb1430b807b4db471975707cb6ce8c36fd`

`JourneyRuntimeEngine.DiscoverAndAcceptAsync` previously captured an iteration timestamp before the asynchronous RIoT vehicle read. The production HTTP adapter stamped a successful observation after the response was read, so a normal fresh observation appeared later than the old reference timestamp and was rejected as `RIOT_VEHICLE_FACT_STALE`.

The minimal product change captures `dynamicFactsNow` immediately after `ReadVehicleAsync` completes and uses it for the initial dynamic-facts validation. The later final admission gates already capture time after their vehicle reads and were unchanged.

## Test-first evidence

The new focused regression is:

`VehicleReadsThatAdvanceClockUsePostReadTimeForAdmission`

The test advances the fixed clock by 1 ms inside every fake vehicle read, before `ObservedAt` is sampled. Against the old product implementation it failed 0/1 because AcceptedDemand remained empty. After the product fix it passed and verifies all relevant secondary effects:

- the fake clock actually advanced during vehicle reads;
- AcceptedDemand exists with status `Accepted`;
- JourneyRuntime exists at `AwaitingPickupArrival`;
- the unique `TO_PICKUP` OrderIntent is `CONFIRMED` with the expected order id;
- exactly one pickup RIoT create was recorded;
- backlog state is `ACCEPTED` with non-null AcceptedAt.

Final clean validation results:

- focused regression: 1/1 PASS, 0 skip;
- complete `JourneyRuntimeWorkerTests`: 33/33 PASS, 0 skip;
- complete `ControlServer.Tests`: 108/108 PASS, 0 skip;
- `dotnet format ControlServer.sln --verify-no-changes --no-restore`: PASS;
- Release non-incremental solution build: PASS, 0 warnings, 0 errors.

## Exact package

A new single-branch clone was detached at exact product commit `193d6bbb1430b807b4db471975707cb6ce8c36fd` and remained clean. Its repository-owned publish script generated a self-contained `win-x64` package.

Independent validation confirmed:

- manifest source commit equals the exact product commit;
- 371 unique declared payload files and 371 actual payload files;
- zero rooted or escaping paths, duplicate paths, missing files, length mismatches, SHA-256 mismatches, undeclared payload files, or declared/actual set differences;
- the only additional file is `deployment-manifest.json`;
- manifest SHA-256 is `2a10eddda8019afb7d228df778d0fdcc8fb3595c07ce8c9083a4b9c11ea8c16b`.

## Deployment and threshold restore

Before deployment, the fixed effective-state inspection returned PASS: both JourneyRuntime layers false, threshold 10%, stop marker present, orchestrator not running, service Running, required ports owned only by the service, and HTTPS live healthy.

The rollback-capable updater from the exact detached clone completed with run id `20260828T061757Z`. Strict result validation confirmed the expected source commit and manifest hash, service Running, protocol `protocol-v0.1.1`, authenticated safety HTTP 200/STOPPED with zero reasons, JourneyRuntime false, and no mutation/order/movement.

The package restored the repository-default base threshold. The fixed `Restore Test Threshold 10` task then completed with return code 0 and a fresh PASS result: both runtime layers false, threshold 10%, service Running, and live HTTP 200.

## Final safety state

The final fixed inspection and explicit atomic read-only preflight both returned PASS:

- both JourneyRuntime layers false, threshold 10%, stop marker present;
- service Running/live and ports 58005/58007 healthy;
- vehicle identity matched, connected, enabled, IDLE, on the expected map, and evidence fresh;
- battery 46%, speed zero, lock clear, and no order/task;
- direct and HTTPS safety both STOPPED with matching source and zero reasons;
- no peer process and no temporary listener;
- no RIoT mutation, no order creation, and no vehicle movement.

The pre-existing preflight executable reports a hard-coded historical `productSourceCommit`; that field was explicitly excluded from deployment identity. Deployment identity is bound by the exact package manifest and strict updater result above.

This deployment does not authorize a real journey. The next real attempt must use a fresh explicit authorization bound to deployed ControlServer `193d6bbb1430b807b4db471975707cb6ce8c36fd` and the selected Onboard commit.

Raw credentials, installed configuration contents, backup paths, vehicle identity, candidate identities, and unrestricted logs are excluded.
