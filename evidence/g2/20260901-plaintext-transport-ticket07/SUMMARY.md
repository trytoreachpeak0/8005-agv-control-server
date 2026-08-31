# Ticket 07 — cross-machine plaintext integration

Run date: 2026-09-01 (local, UTC+08:00)

## Peers

| Role | Machine | Identity |
| --- | --- | --- |
| ControlServer (plaintext) | host `LAB-WIN-01`, `192.168.200.1` | `ControlServer_MVP@65841df04aa6184106cfd407738fc2ebeb5d968c` |
| Onboard (plaintext) | guest `PLAINTEXT-OBU-07`, `192.168.200.50` | `OnboardHmi_MVP@238b46eb2c9ae90584e4288a782176f66b7de942` |
| Onboard (TLS era, mismatch A only) | same guest | `OnboardHmi_MVP@31263b1ffd372db1f27af5e1143ebad7e7679715` |
| ControlServer (TLS era, mismatch B only) | same host, ports 58105/58107 | `ControlServer_MVP@3d8b00c7558ae700358f1f995a5ac75d12a3250c` |

The guest is a Hyper-V VM created for this ticket by copying the `gpt_win11.vhdx` parent disk;
the golden VM itself was never started, its checkpoint was never merged, and nothing was written
into it. The two machines meet on the `Huawei` internal switch, so `192.168.200.1` and
`192.168.200.50` are genuinely different hosts — **no loopback shortcut was used anywhere**.

Both onboard builds were published from a throwaway clone under `F:\w2g-ticket07`. The
`8005-agv-onboard-hmi` working tree stayed at 0 changed paths with its HEAD unmoved.

## Reachability criterion

`reachability-and-red-controls.txt`. Reachability is judged **only** by a returned body:

- GREEN `GET /health/live` → `200 {"status":"live"}`
- GREEN `GET /api/onboard/v1/vehicle-safety` → `200` with the projection JSON
- RED, port with no listener → `由于目标计算机积极拒绝，无法连接。 (192.168.200.1:58099)`
- RED, address with no host → `The request was canceled due to the configured HttpClient.Timeout`
- RED, no `Authorization` header → `HTTP 401`

`ping 192.168.200.1` and `ping 192.168.200.77` happen to answer truthfully on this internal
segment, but ping was still not used as a criterion — the body round-trip is.

## What the plaintext link carried

`green-side-session-projection.txt`, `plaintext-server-console.log`, `green-side-onboard*.log`.

Server log line: `Onboard NDJSON listener started on 192.168.200.1:58005; transport=plaintext`
and `Now listening on: http://192.168.200.1:58007`.

Four sessions were established across the run, and `ProtocolInbox` shows one full opening set per
session — `SessionHello: 4`, `CapabilitySnapshot: 4`, `SafetyStateSnapshot: 4`,
`RecoveryStateReport: 4` — plus `Heartbeat: 108` and `SafetyStateChanged: 89`. Every one of those
messages crossed the machine boundary as plaintext NDJSON.

The onboard peer read the vehicle-safety projection over plaintext HTTP with `vehicleKey`
matching `BROKERX-0c20ff0600d644869a6a80c186065d85` exactly and a fresh `observedAt`.

## Disconnect and reconnect

`green-side-onboard-reconnect.log`. Stopping the server mid-run produced, on the onboard side:

```
上层会话不可用：由于目标计算机积极拒绝，无法连接。。将在2秒后重连。
```

repeated on the configured `reconnectDelaysMs`, then `上层会话已建立：generation=2`. Session
generation advanced 1 → 2 → 3 → 4 across the run with no protocol-layer anomaly. Note the
plaintext failure shape: an outright connection refusal, with no TLS handshake error to fall
back on.

## Durability of the run

`green-side-detached-run.txt`. Every onboard process started from inside a PowerShell Direct
session dies when that session closes (the logs end on a clean line, not a crash). The final
run was therefore detached through `Win32_Process.Create` and then sampled from a **separate**
session: the process survived, `generation=4`, and both sockets
(`:58005` and `:58007` to `192.168.200.1`) were observed from that independent session.

## Readiness — partially reached, boundary stated

The ticket asks for the session to reach `Ready` rather than stay at `RecoveryRequired`. It
did not, and the reason is **not** the transport:

`Readiness: RecoveryRequired`, `ReasonCode: DEPARTURE_SAFETY_NOT_READY`,
`SafetyReasonCodesJson: ["LOCK_NOT_CLOSED","SLOT_STATE_UNKNOWN","UNLOCK_OUTPUT_NOT_RESET",
"VEHICLE_STATE_UNKNOWN"]`.

- The first three are the guest's missing Modbus IO module (`ModbusTcpIoModuleClient` times out
  continuously). They are local onboard IO facts and are unrelated to either link under test.
  Clearing them needs real slot hardware, which is what ticket 10's field acceptance covers.
- `VEHICLE_STATE_UNKNOWN` **was cleared** while the upstream was reachable, and that was proved
  with a control rather than asserted:

| | upstream RIoT | `SafetyReasonCodes` |
| --- | --- | --- |
| GREEN | reachable | `LOCK_NOT_CLOSED, SLOT_STATE_UNKNOWN, UNLOCK_OUTPUT_NOT_RESET` |
| RED | stopped | the same three **plus** `VEHICLE_STATE_UNKNOWN` |

  So the projection delivered over plaintext HTTP really does drive the server's session safety
  state — the detector fires, it is not a stuck-green shell. It reappears intermittently because
  the stand-in RIoT answers in ~2 s (`responded 200 in 2022 ms`), which crowds the onboard
  `maximumEvidenceAgeMs: 5000` budget.

The upstream RIoT was stood in for by `fake-riot.py`, which answers the exact Round-41 predicate.
RIoT sits upstream of the ControlServer and is not part of either link under test, but the
projection is fail-closed, so with no reachable RIoT it can only ever answer `UNKNOWN` and the
readiness assertion could not be exercised at all. `fake-riot-requests.log` records the real SDK
routes it was asked for.

## Mismatch cross-reference (for ticket 04's manual)

The two sides do not negotiate. Both directions were run and the real error text captured.

### A — TLS-era onboard (`useTls=true`) → plaintext server

| Side | Text |
| --- | --- |
| Onboard | `上层会话不可用：Received an unexpected EOF or 0 bytes from the transport stream.。将在2秒后重连。` |
| Server | `Onboard connection ended with a protocol or transport error.` |

`SessionHello` did **not** increase: nothing from that build reached the protocol layer. The
onboard peer retries forever and never exits — a silent failure shape, and the text says nothing
about TLS, so the field can easily misread it as a network fault.

### B — plaintext onboard → TLS-era server

| Side | Text |
| --- | --- |
| Onboard, link A | `上层会话不可用：ControlServer在会话恢复期间关闭了连接。。将在2秒后重连。` |
| Onboard, link B | `An error occurred while sending the request.` |
| Server | `System.Security.Authentication.AuthenticationException: Cannot determine the frame size or a corrupted frame was received.` |

`mismatch-b-tls-era-session-projection.txt` shows `ProtocolInbox: 0 rows` and
`SessionRecoveries: 0 rows`.

**The onboard-side wording in direction B is the dangerous one**: "ControlServer 在会话恢复期间
关闭了连接" reads like a business-layer recovery problem, not a transport-shape mismatch. The
server side is the only place that names the real cause. The manual should tell the field to read
the **server** log for this class of failure.

Also worth recording for the manual: the plaintext build's `VehicleSafetySettings.Validate`
accepts only `Uri.UriSchemeHttp`, so "just point it at https" is not even expressible — a
misconfigured site will produce plain HTTP against a TLS port, which is exactly what was run here.

## Configuration surface, read back on the guest

`environment` was set to **Production** on purpose: that is the only mode in which
`WireToGateSettings.Validate` and `VehicleSafetySettings.Validate` run `IsForbiddenProductionHost`,
which rejects loopback outright. A loopback shortcut could not have passed this configuration.
`RuleGatewaySettings` and `IoModuleSettings` only reject placeholder values in production, not
loopback, so they could stay on `127.0.0.1`.

Read-back of the running config showed `useTls` absent, `serverCertificateSha256` absent, and no
`https` anywhere in the file.

Incidental finding on the TLS-era build: its shipped `appsettings.json` carries `useTls` but has
**no** `serverCertificateSha256` key at all, and its `vehicleSafety.endpoint` is `https://`.
Assigning the missing key on the `PSCustomObject` threw `SetValueInvocationException`, exactly as
the map's note describes; `Add-Member` was required.

## Isolation

- The production `ControlServer.Host` kept `127.0.0.1:58005/58007` throughout and was never
  touched; the ticket-07 server bound `192.168.200.1` on the same port numbers.
- `CurrentUser\Root` held **44** certificates before the TLS-era experiment, while it ran, and
  after it was torn down. The throwaway PFX was generated straight to a file through the .NET
  APIs and never imported into any store, so ticket 03's trust-store evidence is intact.
- No credential, certificate or password is present in this directory (verified: 0 occurrences
  of the shared credential, 0 key-material files).
