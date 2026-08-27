# Map 25 read-only identity and station readiness

Result: `READ_ONLY_MAP25_IDENTITY_AND_STATION_PARSE_WITH_STRUCTURAL_GAPS`

This evidence is a read-only prerequisite check. It is not a formal G3 slice
PASS, does not authorize RIoT order creation, and did not create an order or
move a vehicle. The supplied CallApiKey was read through hidden console input
for the two GET requests. At the deployment owner's explicit instruction it was
then persisted as the Windows User-scope environment variable
`CONTROL_SERVER_RIOT_CALL_API_KEY`; its value was not written to Git or to this
evidence artifact. Start the Host from a newly opened terminal or refreshed
sign-in session so its process environment inherits that user-level value.

## Bound identity

- AGV: `老厂前线新多仓位1`
- RIoT vehicleKey: `BROKERX-0c20ff0600d644869a6a80c186065d85`
- First lifecycle generation: `1`
- Map: `mapId=25`, `mapIdentity=老厂前线new`
- Fixed gate: `关卡`, RIoT station id `210`

The RIoT vehicle read returned success and the exact device key. At observation
time the vehicle was online, enabled, `IDLE`, speed `0`, lock status `0`, with no
active orderTaskId. Its reported currentPosition was `0`; this observation does
not establish arrival at a business Station.

## Map station catalog

- Approved read-only operation: `GET /api/imap/v1/mapInfo/stations/25`
- RIoT response code: success
- Complete response station count: `206`
- Exact `关卡/210` matches: `1`
- Ordinary/public stations were excluded from AREA parsing.

## Current MesIngest WIRE_TO_GATE resolution

The localhost MesIngest v2.2 catalog contained 17 current WIRE_TO_GATE
AREA/EQP pairs. Ten uniquely resolved on Map 25, seven had no matching
AreaNamedMachineStation, and none were ambiguous.

| AREA | EQP | Map 25 Station |
| --- | --- | --- |
| N1-3 | 2QHE54 | `N1-3/3` |
| N1-7 | 2QHE51 | `N1-7/7` |
| N15-8 | 2QHB54 | `N15-8_N16-8/89` |
| N16-2 | 2QHB103 | `N15-2_N16-2/95` |
| N18-1 | 2QHB80 | `N17-1_N18-1/97` |
| N21-2 | 2QHB20 | `N21-2/118` |
| N3-8 | 3QHS6017 | `N2-8_N3-8/24` |
| N5-12 | 2QHE04 | `N4-12_N5-12/44` |
| N6-1 | 2QHE32 | `N6-1_N7-1/49` |
| N7-7 | 2QHE14 | `N6-7_N7-7/55` |

No Map 25 match was present for `D11-10/3QHS3990`, `D11-15/3QHS4004`,
`D12-15/3QHS3999`, `N22-1/2QHB08`, `N25-5/2QHB44`, `N26-8/2QHB51`, or
`Q18-15/3QHS4112`. They remain fail-closed until map maintenance supplies a
unique requirements-compliant station or the deployment owner confirms those
AREA values are outside Map 25's DispatchZone.

## Remaining gates

- Open a new terminal (or refresh the sign-in session) before integration, then
  start the Host so it inherits the provisioned
  `CONTROL_SERVER_RIOT_CALL_API_KEY` user environment variable.
- Confirm the seven missing AREA values are map defects or outside this Map's
  DispatchZone.
- Supply the approved battery threshold, PACKAGE capacity rules,
  SUBLOT_BOX_COUNT path, Onboard credential, and real stopped/parking provider.
- Obtain separate authorization before any RIoT mutation or vehicle movement.
