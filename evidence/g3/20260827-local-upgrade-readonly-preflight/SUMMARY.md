# Local ControlServer upgrade and Map 25 read-only preflight

Date: 2026-08-27 (Asia/Shanghai)

Result: local deployment and read-only preflight PASS; formal W2G G3/RC remains INCONCLUSIVE.

## Bound identities

- Deployed product source: `ControlServer_MVP@5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- Upgrade tooling: `ControlServer_MVP@c354ff8a85c5e5e389a9c926e24f68e5f60caa7d`
- Package manifest SHA-256: `1c75c6146ab55972147ef9c721f98c54c6c412d5e202d063433ad79b1700fdd1`
- Preserved production configuration SHA-256: `da3d2fd90f6a4f5f097fd4cd8c7d2d3b772d5a130aae59a8de342eece6679734`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Current protected Onboard remote head, read-only checked immediately before evidence publication: `OnboardHmi_MVP@15c6387801fa2154fb69441eac460fea9d0999c5`

## Product defect and regression gate

The deployed host initially returned HTTP 500 from the local safety projection. An elevated foreground diagnostic
identified ambiguous constructor activation for the typed `HttpRiotMovementGateway` client. Product commit `5c72621`
marks the `(HttpClient, TimeProvider)` constructor with `ActivatorUtilitiesConstructor` and adds
`TypedHttpClientResolvesWhenTimeProviderIsRegistered`. The focused test reproduced the exact DI failure before the fix
and passed 1/1 after it. The post-fix Release gate passed 102/102 tests with zero failures and zero skips; Release build
reported zero warnings and zero errors, and format passed.

## Reversible upgrade

`Update-ControlServerLocal.ps1` validates the package manifest, requires `JourneyRuntime.enabled=false`, stops the
service, stores ACL-restricted install/data backups, stages and replaces the package while preserving production
configuration, runs live/version/restart and optional authenticated read-only safety checks, and restores both install
and data on failure.

Two initial executions exposed Windows PowerShell 5.1 compatibility gaps in the new operational script. Both reached
`rollback-complete`, restored the previous service, and produced no success result. Commits `0cafc0c` and `c354ff8`
fixed full HTTP type naming and explicit `System.Net.Http` assembly loading. The final execution passed:

- service `8005 AGV ControlServer`: Running / Automatic / LocalSystem;
- process owns loopback TLS ports 58005 and 58007;
- `/health/live` and `/version` pass through the installed `localhost` trust chain;
- deployed source is `5c72621`, and a stop/start/restart lifecycle passed;
- authenticated `GET /api/onboard/v1/vehicle-safety` returns HTTP 200;
- JourneyRuntime stayed disabled; no RIoT mutation, order creation, or vehicle movement occurred.

The original local upgrade result outside Git had SHA-256
`764fdc7d46498bd4c2f0ee33c24a38e23499321a9876aa335e56f935c1e52a4a`; its diagnostic log had SHA-256
`0f6dd6b93120466f274654c3c76ae3590352d00c5d6c43e57a1942e279081229`.

## Same-snapshot read-only field preflight

The probe used the production `HttpMesIngestCatalog`, `HttpRiotMovementGateway`, `MapStationResolver`, and
`PackageCapacitySeed` implementations. Its committed JSON omits Sublot, AREA/EQP, PACKAGE, vehicle key, raw response
bodies, and all credentials.

- MesIngest exact v2.3/schema 29 identity and exact ten-capability set passed at catalog revision 1409; 264 current
  items included 24 WIRE_TO_GATE items. The `SUBLOT_BOX_COUNT` capability was checked, while its previously proven
  business-instance query was deliberately not repeated with a dynamically selected Sublot.
- Map 25 returned 206 stations and the exact `关卡/210` binding. Nine current `N*` AREA groups had unique AREA/EQP;
  eight resolved uniquely, one had no Map station, and none were ambiguous.
- The approved 28 capacity rules covered 4 of 15 distinct current PACKAGE values; 11 remained uncovered. Seven
  current WIRE_TO_GATE items passed the static N*/Map/capacity portion of admission.
- The exact vehicle read was identity-matched, connected, enabled, IDLE, on the expected Map, fresh, speed zero,
  lock clear, and had no active order task. Battery was 34%, satisfying the approved 30% threshold.
- Direct RIoT Round-41 safety and the HTTPS Onboard projection matched, but both correctly remained fail-closed
  `UNKNOWN` because of `RIOT_MOVEMENT_NOT_FINISHED`.

The original sanitized preflight result outside Git had SHA-256
`8f6210dab01f860e288bee0d443ecb963b5c4d6cb2779886bee9b14a43f42a6a`.

## Credential exposure and rotation

During a follow-up reason-code check, PowerShell implicitly rendered an `HttpResponseMessage` and thereby included the
Authorization request header in local command output. The value is not reproduced here and must be treated as
compromised. It was immediately replaced by a new random 256-bit credential in Machine scope and the service-specific
environment, the LocalSystem service was restarted, and the new value authenticated the projection with HTTP 200.
The replacement value was never printed, hashed, written to Git, or placed in this evidence. The rotation result outside
Git had SHA-256 `c2706231e37d14ad2cc5125a0a524fc7469f03a8706a09d5160813c30a989c60`.

## Remaining blockers

This result is a deployment/preflight gate, not a formal slice pass. Formal W2G-IS-00 through W2G-IS-07 and the RC
remain INCONCLUSIVE because one current N* AREA lacks a Map station, eleven current PACKAGE values lack approved
capacity rules, the RIoT motion predicate is still UNKNOWN, the protected Onboard head still needs its production
HTTPS/trust/safety-provider integration and owner confirmation, no matching local Onboard session is READY, and no
separate authorization was granted for RIoT mutation, order creation, or vehicle movement.
