# Map 25 capacity and vehicle-safety field-gate implementation

Date: 2026-08-27  
Integration repository: `https://github.com/trytoreachpeak0/8005-agv-control-server`  
Branch: `ControlServer_MVP`  
Formal result: `INCONCLUSIVE` — this increment is not a W2G-IS-00 through W2G-IS-07 G3 PASS.

## Confirmed decisions implemented

- A `WIRE_TO_GATE` demand reaches pickup resolution only when its live AREA begins with
  uppercase `N`; it must then resolve uniquely against the atomically read Map 25 station
  catalog. D/Q AREA values and unresolved N AREA values fail closed before PACKAGE tracking.
- The minimum battery admission threshold is 30 percent; 30 is inclusive.
- PACKAGE capacity is owned by ControlServer SQLite, not MesIngest. The initial catalog is
  the 27 case-sensitive exact rules plus the approved case-sensitive `TOLL-` prefix rule from
  `mes/reference/package-basket-capacity.csv`. Every seed retains source
  `客户提供花篮容量对照表（2026-07-16迁移）`.
- Basket count remains `ceil(MAX_BOX_COUNT / max_boxes_per_basket)`.
- An unknown PACKAGE cannot be accepted or dispatched. Only an otherwise in-scope N+Map25
  candidate creates or updates a deduplicated row keyed by PACKAGE. The row contains only
  `Package`, `FirstSeenAt`, `LastSeenAt`, and `Status` (`PENDING`/`SUPPLIED`); it intentionally
  does not duplicate Demand, Sublot, or MES facts.
- Controlled CSV import is idempotent for an unchanged rule. A capacity conflict requires a
  higher version, supersedes the prior active row, and preserves history. When a missing
  PACKAGE becomes resolvable, the next poll marks its existing history row `SUPPLIED`.
- ControlServer, not Onboard, reads RIoT. The new Onboard-facing GET projection is HTTPS-only,
  uses the external `CONTROL_SERVER_ONBOARD_CREDENTIAL` Bearer credential, disables caching,
  and never exposes the RIoT CallApiKey.
- `STOPPED` requires the full RIoT Behavior Lab Round-41 composite: exact vehicle identity,
  `IDLE`, no processing order, no non-final vehicle order, zero speed, `MT_FINISHED`, online,
  enabled, emergency OK, brake movable, control OK, and location running. Active motion is
  `MOVING`; missing, failed, contradictory, incomplete, or `MT_NA` evidence is `UNKNOWN`.
  No stale value is reused.

The eight confirmed non-routable observations remain excluded from execution:
`D11-10/3QHS3990`, `D11-15/3QHS4004`, `D12-13/3QHS4003`,
`D12-15/3QHS3999`, `D15-14/3QHS336`, `N22-1/2QHB08`,
`Q18-10/3QHS4255`, and `Q18-11/3QHS4257`.

## Persistence and operations

- EF migration `PackageCapacityAndSafetyProjection` creates `PackageCapacityRules` and
  `MissingPackages` and inserts the 28 approved v1 rules.
- `scripts/Import-PackageCapacity.ps1` wraps the controlled six-column CSV importer:
  `pattern,match_type,max_boxes_per_basket,source,status,note`.
- The projection is default-disabled. Enabling it requires an HTTPS `Health:url`, a server
  certificate path, and a populated external credential variable. Machine-scope credential
  verification reported present with length 64; neither the value nor a hash was printed or
  committed.

## Validation

- `dotnet format ControlServer.sln --verify-no-changes --no-restore` — PASS.
- Fresh SQLite migration through all nine migrations — PASS, including
  `PackageCapacityAndSafetyProjection`.
- `dotnet build ControlServer.sln --configuration Release --no-incremental -v:minimal` —
  PASS, 0 warnings, 0 errors.
- `dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --configuration Release --no-build -v:minimal`
  — PASS, 101 passed, 0 failed, 0 skipped.
- Five high-risk pseudo-mutations were injected individually and reverted: battery boundary,
  AREA prefix, first missing-PACKAGE status, `MT_FINISHED` versus `MT_NA`, and HTTPS negation.
  One initial assertion gap was found and strengthened; final result was 5/5 killed.
- No MesIngest product/configuration was changed. No protected Onboard or simulator file was
  changed. No RIoT mutation, order creation, or vehicle movement occurred.

## Protected Onboard owner handoff

Current protected remote repository HEAD is Wang Kun's
`8005-agv-onboard-hmi/OnboardHmi_MVP@15c6387801fa2154fb69441eac460fea9d0999c5`.
It includes the current-session-generation recovery replay fix. At that exact HEAD,
`App.xaml.cs` still constructs `UnavailableVehicleSafetySignalProvider`, whose source is
`UNAVAILABLE` and whose state is always `Unknown`.

The Onboard owner must replace that composition root with an `IVehicleSafetySignalProvider`
adapter for `GET /api/onboard/v1/vehicle-safety`, using formal certificate trust and the
external `CONTROL_SERVER_ONBOARD_CREDENTIAL`. It must map only response `STOPPED` to
`VehicleMotionState.Stopped`, preserve the response `observedAt` and `source`, and map
`UNKNOWN`, request failure, authentication failure, TLS failure, stale response, or malformed
response to `VehicleMotionState.Unknown`. The likely owner symbols are:

- `src/SQCD.Agv.Wpf/App.xaml.cs` (`UnavailableVehicleSafetySignalProvider` composition)
- `src/SQCD.Agv.Core/VehicleSafetySignal.cs` (`IVehicleSafetySignalProvider`)

This repository does not modify the protected Onboard repository. Formal G3 remains blocked
until Wang Kun supplies and confirms the exact Onboard commit containing that provider, the
certificate trust/configuration is deployed, a fresh read-only field revalidation succeeds,
and separate movement authorization is granted for any dynamic `MT_RUNNING` → `MT_FINISHED`
qualification run.
