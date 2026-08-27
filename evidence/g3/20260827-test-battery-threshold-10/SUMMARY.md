# Test-only battery threshold 10% and RIoT motion blocker

Date: 2026-08-27 (Asia/Shanghai)

Result: the user-authorized local test threshold change passed; formal W2G-IS-00 through W2G-IS-07 G3 and the
release candidate remain `INCONCLUSIVE`.

## Bound identities and scope

- Deployed ControlServer product: `ControlServer_MVP@5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Configuration result SHA-256: `42a332e5cdccdbeb3b5e541ed410add94fce54a803a99f69ea4da5d54a69f17a`
- Read-only preflight result SHA-256: `a6ac6a1e07423ae818b6c47fc7084967e0baaf657003dc82a35efbdb86fc95ee`
- The change applies only to the currently installed local test instance. The repository default remains 30%.
- JourneyRuntime remained disabled. No RIoT mutation, order creation, or vehicle movement was authorized or performed.

## Configuration change and rollback point

An explicit UAC-approved operation changed deployed `JourneyRuntime.minimumBatteryPercent` from 30 to 10, preserved
the prior configuration at
`C:\ProgramData\8005 AGV\ControlServer\config-backups\appsettings.before-battery10-20260827T100502112Z.json`, and
restarted the `8005 AGV ControlServer` Windows Service. Post-change checks confirmed:

- the configured minimum is 10 and JourneyRuntime is still disabled;
- the service is `Running`;
- `https://localhost:58007/health/live` returned HTTP 200;
- no configuration body, credential, key, or secret was printed or committed.

The backup is the exact recovery point for restoring the local test instance to its prior 30% setting.

## Read-only post-change snapshot

At `2026-08-27T10:05:31.8390677+00:00`, the vehicle battery was 19%, so it passed the temporary 10% test threshold.
MesIngest catalog revision 1476 contained 11 WIRE_TO_GATE items that passed the static N-scope, Map, and approved
capacity filters. The catalog remained dynamic: 13 of 14 current N-scoped AREA groups resolved uniquely and one did
not, while 6 of 13 distinct PACKAGE identities were covered by approved rules.

The direct RIoT Round-41 read and authenticated HTTPS projection still agreed on fail-closed `UNKNOWN` with the sole
reason `RIOT_MOVEMENT_NOT_FINISHED`.

## Why the RIoT blocker remains

ControlServer reads the RIoT vehicle-safety and non-final-order endpoints without mutation. A `STOPPED` projection
requires the full composite to pass, including `movementState == MT_FINISHED`. The current observation passes the
other recorded static predicates but RIoT does not report `MT_FINISHED`; previous exact field observation was
`MT_NA`. Treating `MT_NA` as stopped or weakening this predicate would remove an accepted fail-closed safety boundary
and is not an acceptable test workaround.

The next safe action belongs to the RIoT/vehicle operator: determine why the stationary vehicle reports `MT_NA`,
restore or refresh its RIoT telemetry so a genuinely completed motion cycle reports `MT_FINISHED`, and then repeat the
read-only projection check. If producing `MT_FINISHED` requires moving the vehicle, that requires separate explicit
movement authorization and field safety prerequisites; this configuration change does not grant it.
