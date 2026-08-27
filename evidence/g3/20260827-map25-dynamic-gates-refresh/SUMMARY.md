# Map 25 dynamic field-gate refresh

Date: 2026-08-27 (Asia/Shanghai)

Result: sanitized read-only refresh PASS; formal W2G-IS-00 through W2G-IS-07 G3 and the release candidate remain
`INCONCLUSIVE`.

## Bound identities

- Deployed ControlServer product: `ControlServer_MVP@5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Sanitized result SHA-256: `89003303c0b1a346f313e1d3b2797f734b4004530956b7c37d260ec2bc4ad899`

The probe used the production MesIngest, Map station, RIoT vehicle/safety, package-capacity, and authenticated Onboard
safety-projection implementations. The committed result omits Sublot, AREA/EQP, PACKAGE, vehicle identity, raw response
bodies, and credentials. The exact uncovered PACKAGE identities were retained only in a non-Git owner-action artifact.

## Atomic read-only observation

At `2026-08-27T09:52:36.4121134+00:00`:

- MesIngest v2.3/schema 29 was at catalog revision 1469 with 271 current items, including 18 WIRE_TO_GATE items.
- Map 25 still contained 206 stations and the exact fixed gate binding. All 11 current N-scoped AREA groups had one
  AREA/EQP identity and resolved uniquely; there were zero missing or ambiguous pickup stations.
- The 28 approved capacity rules covered 5 of 11 current distinct PACKAGE identities. Six remained uncovered, and 10
  current WIRE_TO_GATE items passed the static N-scope, Map, and capacity portion of admission.
- The bound vehicle remained identity-matched, connected, enabled, IDLE, on the expected Map, fresh, speed zero,
  lock-clear, and without an active order task. Its battery was 21%, below the approved 30% threshold.
- The direct RIoT Round-41 safety read and the authenticated HTTPS projection agreed on fail-closed `UNKNOWN` with
  `RIOT_MOVEMENT_NOT_FINISHED`.
- JourneyRuntime remained disabled. No RIoT mutation, order creation, or vehicle movement occurred.

The official `ControlServer.sln` Release build was also rechecked and passed with zero warnings and zero errors; no
product source was changed.

## Decision impact

The previously observed missing Map station is not a current blocker, confirming that the MES/Map catalog is dynamic
and must be atomically reread before any formal run. Formal qualification is still blocked by the six unapproved
PACKAGE capacity mappings, battery below the approved threshold, the external RIoT motion predicate, and the absence
of fresh authorization for RIoT mutation, order creation, or vehicle movement. The six PACKAGE identities require a
named business owner to provide or approve the exact boxes-per-basket values; they must not be inferred from naming or
history.
