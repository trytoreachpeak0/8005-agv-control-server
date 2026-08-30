# Issue 14 usable-MVP acceptance, no movement

`Invoke-UsableMvpAcceptance.ps1` deploys the release candidate `w2g-rc-20260830-81cb9cf`
(`ControlServer_MVP@81cb9cf` + `OnboardHmi_MVP@304e6ad` + `protocol-v0.1.1`) from a clean state,
following the public `RELEASE-CANDIDATE.md` procedure, and qualifies everything from installation up
to and including demand intake. `assertions.json` is what that run emitted: **23 PASS, 0 FAIL,
5 INCONCLUSIVE**. The RIoT create gate stayed closed, `RiotDispatchAuditEvents` finished at 0, and no
vehicle was moved.

## What is new relative to ticket 13

Ticket 13 left `SESSION-READINESS`, `DEPLOY-HEALTH` and the onboard configuration checks FAIL, and
`ADAPTER-RIOT-READONLY` INCONCLUSIVE, because the run copied the packaged development
`appsettings.json` and left `vehicleSafety` disabled. Ticket 26 traced that to configuration and
proved the transition by replaying a captured handshake. This run produces the transition live:

- `SAFETY-STOPPED` — the real RIoT reports the configured vehicle `STOPPED` with no reason codes,
  read through the pinned HTTPS projection.
- `SESSION-READY` — `上层会话已建立：generation=1，readiness=Ready` on a clean install.
- `HEALTH-READY` — `/health/ready` returns `200 ready`, against `503
  RECOVERY_HANDSHAKE_REQUIRED` recorded on the same endpoint minutes earlier in the same run.
- `DEMAND-ACCEPTED` — `AcceptedDemands=1`, `JourneyRuntimes=1`, taken from a real backlog of 303.
- `ADAPTER-RIOT-READONLY` — `StationTaskTypeAdmissions=205`, derived from the real RIoT map station
  catalog. This is the durable RIoT-derived row ticket 13 could not reach.

Restart was exercised on the same data root: session generation `1 -> 2`, readiness `Ready` again,
`ProtocolInbox 34 -> 52`, onboard journal grown rather than recreated.

## The workstation path to RIoT, and two detectors it invalidates

`riot-path-red.json` records the state this session started in. Clash was in global mode, so every
connection to `172.19.206.222` was taken by the Clash TUN interface; independently `vEthernet
(Default Switch)` owns `172.19.192.1/20`, which contains the site RIoT address. The safety projection
returned `UNKNOWN / RIOT_READ_TIMEOUT` and nothing downstream was reachable.

Two probes were green throughout that outage and must not be used as RIoT reachability evidence on
this workstation: ICMP answered 8/8 at 0.6 ms, and TCP connect succeeded on ports 8899, 9, 12345 and
65001 as well as on `172.19.206.99`, an address nothing occupies. **Ticket 13 recorded `NET-RIOT` as
PASS from a TCP-connect probe whose control was port 8899 returning False; that control does not hold
under this network state.** Only an HTTP round trip that returns a body distinguishes the two states,
which is what `SAFETY-STOPPED` now asserts.

## The production guard that stopped the first attempt

The first attempt configured the onboard from `appsettings.Production.template.json` with
`wireToGate.host = localhost`. The onboard refused to start — `软件无法启动，请联系维护人员检查程序配置` —
and produced no log directory at all, because `OnboardSettings.Load` throws before the logger exists.
`Configuration.cs IsForbiddenProductionHost` rejects loopback for both `wireToGate.host` and
`vehicleSafety.endpoint` when `environment=Production`. That is the deployment guard working. It does
mean a single-machine acceptance must bind a non-loopback local interface; this run uses
`192.168.200.1`, a host-internal Hyper-V switch, so nothing is published to the site network.
`CFG-PRODUCTION-GUARD` records it with that failed attempt as its control.

## Falsifiability

`Invoke-AcceptanceRedSide.ps1` re-runs the same deployment twice, changing exactly one field each
time. `mutations.json` holds the result; both went red:

| id | mutation | result |
| --- | --- | --- |
| `M1-TLS-PIN` | one hex digit of `wireToGate.serverCertificateSha256` flipped | no session line at all |
| `M2-VEHICLE-KEY` | `vehicleSafety.expectedVehicleKey` changed to another well-formed key | session established, readiness stayed `RecoveryRequired` |

M1 shows `SESSION-ESTABLISHED` is load bearing on the pinned certificate; M2 is the single-variable
red for `SESSION-READY` and confirms that readiness is driven by the safety evidence rather than by
the session existing. `RC-HASHES` carries its own control: the same comparison over a one-byte-flipped
copy reports the mismatch.

## What this run does not qualify

| id | why |
| --- | --- |
| `INSTALL-AS-SERVICE` | manual section 4 needs an elevated PowerShell; this session has no administrator token. The run starts the packaged host directly, which qualifies the binaries but not the installer. |
| `PERSISTENT-LOGS` | the NDJSON file sink is configured by `appsettings.Production.json`, which only the installer writes. Follows from `INSTALL-AS-SERVICE`. |
| `MOVEMENT-CLOSED-LOOP` | pickup, multi-slot load, movement, gate batch unload and atomic completion need the create gate open and a per-run safety GO. `SAFETY-NO-CREATE` is the positive evidence that this run did not move the vehicle. |
| `HW-ONBOARD-TARGET` | development workstation, not the vehicle terminal. |
| `HW-REAL-IO` | eight-slot simulator on `127.0.0.1:1502`, not real IO modules, wiring, locks or light curtains. |

`host.out.excerpt.log` is the server log with the EF SQL removed. It records every adapter HTTP call
with URL, status and latency, which is what makes an adapter failure diagnosable; it still never
prints a protocol message type name, so the onboard log remains the only place a session fact is
named. `onboard.log` is the onboard log from the same run.

W2G-IS-00 to 07 and the RC remain `INCONCLUSIVE`. No product code was changed by this run.
