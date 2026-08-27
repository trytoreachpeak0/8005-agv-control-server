# Map 25 field-gate snapshot after MesIngest v2.3 deployment

Result: `FIELD_GATES_OPEN_FORMAL_G3_INCONCLUSIVE`

This is a read-only prerequisite snapshot. It is not a formal W2G-IS-00
through W2G-IS-07 G3 PASS and does not authorize RIoT mutation, order creation,
or vehicle movement. No credential, Sublot, DemandId, history epoch, or raw RIoT
response is stored here.

## Bound inputs

- Live MesIngest Windows Service: `Running`, automatic, `LocalSystem`
- Live MesIngest contract: `2026.08.new-mes-ingest.v2.3`, schema `29`
- Required capability observed: `SUBLOT_BOX_COUNT|1.0|GET /api/v2/sublot-box-count`
- RIoT Map: `mapId=25`
- Read-only RIoT operation: `GET /api/imap/v1/mapInfo/stations/25`
- Map Station count: `206`
- Canonical sorted `id + name` Station snapshot SHA-256:
  `573d87522e35c75d1f8b20ccdd57cfd4c6b680618348f10e7cd29dfbb58dd887`
- Exact fixed gate matches: one `关卡/210`

The RIoT CallApiKey was read from the existing process environment and used
only as a Bearer credential for the approved GET. Its value was not printed or
persisted by this check.

## Current AREA/EQP resolution

MesIngest is a live projection, so its catalog changed during the observation
window. At `2026-08-27T05:40:37Z`, revision `1260` contained 25 distinct current
WIRE_TO_GATE AREA/EQP pairs: 18 uniquely matched Map 25, seven had no match, and
none were ambiguous. At revision `1262`, the catalog contained 15 distinct
pairs: seven uniquely matched, eight had no match, and none were ambiguous.
Four samples from `2026-08-27T05:42:30Z` through `05:42:46Z` remained stable at
revision `1262` with these eight unmatched pairs:

| AREA | EQP |
| --- | --- |
| D11-10 | 3QHS3990 |
| D11-15 | 3QHS4004 |
| D12-13 | 3QHS4003 |
| D12-15 | 3QHS3999 |
| D15-14 | 3QHS336 |
| N22-1 | 2QHB08 |
| Q18-10 | 3QHS4255 |
| Q18-11 | 3QHS4257 |

Compared with the earlier
[`Map 25 read-only identity and station readiness`](../20260827-map25-readiness/SUMMARY.md),
the number and identity of currently unmatched pairs are not static. The Map
catalog still has 206 Stations, while the MesIngest WIRE_TO_GATE projection has
changed. A formal run must therefore re-read and freeze both catalogs
immediately before acceptance; it must not use the earlier seven-pair list as a
configuration snapshot.

Each current unmatched pair remains fail-closed until the Map owner either
supplies a unique requirements-compliant Station or the deployment owner gives
a durable confirmation that the AREA is outside Map 25's WIRE_TO_GATE
DispatchZone.

## Capacity and battery gates

Revision `1262` exposed 11 distinct PACKAGE values across the 15 distinct
AREA/EQP pairs:

| PACKAGE | Current AREA/EQP pairs |
| --- | ---: |
| `DFNWB1.4*1-8L-C(P0.35T0.37)` | 1 |
| `PDFN5×6-8L(12R)` | 1 |
| `SOP8(150mil)(12R)` | 3 |
| `SOP8(8R)` | 1 |
| `SOP8/PP(150mil)(12R)` | 1 |
| `TO-126` | 1 |
| `TO-220-3L-C(T0.5mm)` | 3 |
| `TO-247B-3L` | 1 |
| `TO-252-2L(8R)` | 1 |
| `TO-263-2L-B` | 1 |
| `TSOT-23-5L` | 1 |

The current production configuration remains deliberately disabled with
`JourneyRuntime.enabled=false`, `minimumBatteryPercent=0`, and an empty
`packageCapacityRules` collection. The approved requirements define
fail-closed behavior but do not supply a numeric minimum battery threshold or
PACKAGE-to-boxes-per-basket values. These values cannot be inferred from the
live catalog or `SUBLOT_BOX_COUNT`; they require named deployment-owner
approval and an immutable configuration identity before runtime enablement.

## Onboard gate

The protected Onboard owner branch was checked read-only at
`OnboardHmi_MVP@15c6387801fa2154fb69441eac460fea9d0999c5`. Production WPF still
constructs `UnavailableVehicleSafetySignalProvider`, so stopped/parking facts
fail closed. The named `CONTROL_SERVER_ONBOARD_CREDENTIAL` is absent from the
current Process, User, and Machine environment scopes. No Onboard file was
changed.

The Onboard owner must supply both the named credential through a secure
channel and a real, fresh stopped/parking signal provider tied to an exact
owner commit before formal G3.

## Gate disposition

- MesIngest v2.3 and `SUBLOT_BOX_COUNT`: ready; do not redo.
- Map 25 identity and fixed gate: read-only check passed.
- Current pickup mapping: blocked by eight revision-1262 unmatched AREA/EQP
  pairs; the set is live and must be re-frozen immediately before a formal run.
- Battery threshold: blocked; no approved numeric value exists in the current
  authority set.
- PACKAGE capacity: blocked; no approved runtime rules exist for the current
  PACKAGE set.
- Onboard credential and real stopped/parking provider: blocked at the Onboard
  owner.
- RIoT mutation, order creation, and vehicle movement: not authorized and not
  attempted.

Formal W2G-IS-00 through W2G-IS-07 G3 and the release candidate therefore
remain `INCONCLUSIVE`.
