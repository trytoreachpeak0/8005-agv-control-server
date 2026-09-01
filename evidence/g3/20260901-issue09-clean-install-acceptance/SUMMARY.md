# Ticket 09 — clean-install acceptance and section 4.5 upgrade rehearsal

Run on 2026-09-01 against the rebuilt plaintext release candidate
`C:\Users\szy\Desktop\w2g-rc-20260901b-19ce7db`
(ControlServer `19ce7db7`, OnboardHmi `238b46eb`, `protocol-v0.1.1`).

| Run | Result | File |
| --- | --- | --- |
| Clean-install acceptance (round 3, final) | **35 PASS / 0 FAIL / 4 INCONCLUSIVE** | `acceptance-round3/assertions.json` |
| Section 4.5 upgrade rehearsal | **18 PASS / 0 FAIL** | `upgrade-rehearsal/upgrade-assertions.json` |
| Red side, single-field mutations | **7 PASS / 0 FAIL** | `red-side/red-side-controls.json` |
| Round 1, kept as the harness-defect record | 30 PASS / 5 FAIL | `round1-harness-defects/assertions.json` |

Every assertion is emitted by the harness at the moment it is observed. Nothing in this directory is
a table transcribed from results after the fact.

## Why the candidate was rebuilt

`RELEASE-CANDIDATE.md` section 4.5 tells the site to run `.\scripts\Update-ControlServerLocal.ps1`
with the same package-relative prefix as the install command in section 4.2, but
`New-WireToGateReleaseCandidate.ps1` only copied Install, Uninstall and Publish into the candidate.
The documented upgrade path was **unrunnable from the delivered package**. The ticket 08 candidate
`w2g-rc-20260901-238b46e` has the defect; so do `w2g-rc-20260830-81cb9cf` and
`w2g-rc-20260831-31263b1`, but section 4.5 is new this round, so the defect is this round's.

Fixing it means editing the release script and the manual, both of which are hashed into the
candidate, so the candidate was rebuilt at `19ce7db7`. `RC-UPGRADE-SCRIPT-SHIPPED` asserts the fix
and uses the ticket 08 candidate as its control (`present there: False`).

## The rebuild changed the identity label, not the product

`git diff --name-only 56d4b1c..19ce7db -- src tests Directory.Build.props Directory.Packages.props global.json`
lists **0 files**. The two candidates' `controlserver/` trees agree on 373 of 383 files; the ten that
differ are the four own assemblies (`.dll` + `.pdb`, a new MVID per build), the apphost `.exe`, and
`deployment-manifest.json`, which records the hashes.

The apphost is the one file whose hash difference carries meaning, because it is not compiled from
source and embeds `InformationalVersion`. The two are 152064 bytes each and differ in exactly
**38 bytes, all inside the contiguous window 0x24D6C–0x24DBB**, which decodes as
`ProductVersion 1.0.0+56d4b1cc…` against `ProductVersion 1.0.0+19ce7db7…`. Every byte outside that
window is identical.

Consequence recorded rather than papered over: the ticket 13 G3 runs were executed against
`56d4b1c`, and **the last real G3 execution is still that one**. `run-staged-g3.ps1` now pins
`19ce7db7` so future runs build the candidate; the shared binding was moved, the run was not
repeated, and the evidence above is why that is acceptable.

## Clean-install acceptance

Isolated instance `8005 AGV ControlServer Ticket09` on 58505/58507 bound to `192.168.200.1` (a
host-internal Hyper-V switch — `environment=Production` on the onboard refuses a loopback
ControlServer host). Installed and uninstalled by the candidate's own scripts.

Closed this round, both INCONCLUSIVE for ticket 14 because that session had no administrator token:

- `INSTALL-AS-SERVICE` — install result `PASS`, `sourceCommit=19ce7db7…`, service `Running`.
- `PERSISTENT-LOGS` — `controlserver-20260901.ndjson` under the data root, 30461 lines.

The certificate assertions, which are the point of the ticket:

- `INSTALL-CERT-STORES-UNCHANGED` — 0 of 4 observed stores changes its **sorted-thumbprint digest**
  across the install (`CurrentUser\Root` 44, `LocalMachine\Root` 42, both `My` stores 1).
  A digest, not a count: swapping one certificate for another keeps the count and moves the digest.
- `INSTALL-NO-CERTS-DIRECTORY`, `INSTALL-NO-KEY-MATERIAL` — no `certs\`, no key-material file under
  the install, data or backup root.
- `INSTALL-MACHINE-CERT-PASSWORD-UNTOUCHED` — the machine-scope certificate password is neither
  written nor cleared; only presence and an equality result are recorded, never the value.
- `INSTALL-NON-INTERACTIVE` — the whole install ran in a `-NonInteractive` host and returned. A trust
  dialog would have blocked forever.
- `LIFECYCLE-CERT-STORES-UNCHANGED` — 0 store changes across install *through* uninstall.

Independent corroboration, in the spirit of the ticket 13 rule that a hard-coded `tls = $false`
evidence field is not evidence: the 30461-line server log for a full install → session → intake →
restart → uninstall cycle contains **0 lines** matching
`Schannel|SslStream|AuthenticationException|X509|certificate|https://`. See
`acceptance-round3/server-log-excerpt.ndjson.txt`, which carries every Warning/Error line in full.

Plaintext transport, rewritten from ticket 14's HTTPS assertions:

- `HEALTH-LIVE-HTTP` — `{"status":"live"}` / 200 over plain http on `192.168.200.1:58507`.
- `SAFETY-PROJECTION-HTTP-AUTH` — 200 with the onboard credential; **401** for the identical request
  without an `Authorization` header.
- `INSTALL-CONFIG-PLAINTEXT` — none of the four removed certificate keys appears in the emitted
  `appsettings.Production.json`; `Health:url` is `http://192.168.200.1:58507`.
- `CFG-ONBOARD-PRODUCTION` — the onboard configuration built from the packaged production template
  has 0 surviving `REPLACE_` placeholders and reintroduces none of `useTls`,
  `serverCertificateSha256`, `requireHttps`.

Session, readiness and restart, all against the **installed service**:

- `SESSION-ESTABLISHED` — `上层会话已建立：generation=1，readiness=Ready。`
- `SESSION-READY` / `HEALTH-GATED-BEFORE` / `HEALTH-READY` — 503 `RECOVERY_HANDSHAKE_REQUIRED`
  before any session, 200 after, on the same endpoint minutes apart in the same run.
- `SAFETY-STOPPED` — the real RIoT reports `motionState=STOPPED`, no reason codes,
  `source=RIOT_BEHAVIOR_LAB_R41`, observed within 30 s.
- `RESTART-SERVICE` / `RESTART-SESSION-RECOVERED` / `RESTART-HEALTH-READY` /
  `RESTART-STATE-PRESERVED` — `Restart-Service` (the documented section 5 operation, not a process
  kill); generation 1 → 2, readiness back to `Ready`, `ProtocolInbox` 34 → 49, journal not recreated.
- `UNINSTALL-SERVICE` — result `PASS`, service absent, install root and data root removed.
- `PRODUCTION-UNAFFECTED-INSTALL` / `PRODUCTION-UNAFFECTED-FINAL` — 0 drift against the pre-run
  snapshot: PID 8632, listeners `::1:58007 127.0.0.1:58005 127.0.0.1:58007`, certificate password
  value unchanged. Listeners are taken **by service PID**, not by process name — ticket 03's snapshot
  used the name, and an isolated probe running the same executable folded its own ports into the
  production reading.

### Demand intake: what this run can and cannot qualify

The installer deliberately writes `JourneyRuntime.enabled=false` (manual section 11: no orders, no
vehicle movement). Demand intake is a direction this ticket must keep, so `JOURNEY-RUNTIME-ENABLED`
records an explicit deviation: the flag is flipped **on the isolated instance only**, while
`RiotCreateDispatch.enabled` and `RiotAbsentAtObservationCreateExperiment.enabled` stay at the
packaged `false`. The runtime readback for the flip is in the log: three
`Journey runtime is disabled; MesIngest polling and movement dispatch are fail-closed.` warnings at
02:23:45–49, during the installer's own lifecycle checks, and **none after** the flip and restart.

- `ADAPTER-MESINGEST` — `JourneyBacklog=264`, against `StationOperations=0` on the same database.
- `ADAPTER-RIOT-READONLY` — `StationTaskTypeAdmissions=205`, against `RiotDispatchAuditEvents=0`.
- `DEMAND-DECISION-REACHED` — **PASS**. 12 `WIRE_TO_GATE` demands present, every one carrying a named
  decision (`OUT_OF_SCOPE_AREA=10`, `AREA_STATION_NOT_FOUND=1`, `BATTERY_POLICY_NOT_SATISFIED=1`),
  0 backlog rows left without a reason code. The control is that every non-`WIRE_TO_GATE` demand is
  attributed to `OUT_OF_SCOPE_WORK_TYPE`, so the classifier discriminates by work type instead of
  stamping one reason on everything.
- `DEMAND-ACCEPTED` — **INCONCLUSIVE**, not FAIL. `AcceptedDemands=0`: no demand the live MES held at
  run time satisfied the site conditions. Which demands MES holds is not a property of this
  candidate. Ticket 14's run happened to catch a qualifying one; the round-2 snapshot of this run saw
  43 `WIRE_TO_GATE` demands and declined all 43 individually, by name. Forcing this green would mean
  manufacturing a demand.

The remaining three INCONCLUSIVE are the same named external qualifications ticket 14 recorded:
`MOVEMENT-CLOSED-LOOP` (ticket 10 — the RIoT create gate stayed closed and no per-run safety GO was
given; `SAFETY-NO-CREATE` with `RiotDispatchAuditEvents=0` is the positive evidence that nothing
moved), `HW-ONBOARD-TARGET`, `HW-REAL-IO` (simulator only).

## Section 4.5 upgrade rehearsal

Ticket 03 left three segments of the upgrade with **no execution evidence at all**, because
`Update-ControlServerLocal.ps1` hard-coded the production service name and roots and so had exactly
one possible target. Ticket 09 parameterized it — `-ServiceName`, `-InstallRoot`, `-DataRoot`,
`-BackupRoot`, `-CertificatePasswordVariable`, all defaulting to the values they replaced — and this
run exercises all three on an isolated instance.

`-CertificatePasswordVariable` is not cosmetic: without it the rehearsal would clear the machine-scope
secret the running production service depends on. The probe variable
`CONTROL_SERVER_TICKET09_PROBE_CERT_PASSWORD` is set to a generated value before the upgrade and
removed after; the code path executed is identical, only the name differs.

- `LEGACY-INSTALL` — a real TLS-era instance from `w2g-rc-20260831-31263b1` (ControlServer
  `9daeef43`), `certs\` holding `localhost.pfx`, `localhost-development-root.cer`,
  `localhost-development-root.pem`, and all four soon-to-be-removed keys present in its
  configuration. This is the shape a site upgrades from, not an approximation of it.

**Failing upgrade and rollback** — against a package whose manifest is internally consistent (the
hashes were regenerated) but whose `appsettings.json` is not valid JSON, so the failure lands
*after* the backup, which is the only way to reach the rollback path:

- `UPGRADE-FAILS-ON-BAD-PACKAGE` — the diagnostic records
  `preflight-complete > service-stopped > backup-complete > staging-complete removedKeys=4
  healthUrlRewritten=True > certificate-cleanup-complete directory=True password=True >
  replacement-installed > failure: … cannot be started … > rollback-complete`.
- `UPGRADE-ROLLBACK-RESTORES-INSTALL` — **0 differences** between the install tree before the upgrade
  and after the rollback. The control is `UPGRADE-REPLACED-BINARIES` further down, where the same
  comparer reports 12 differences, so it is capable of reporting one.
- `UPGRADE-ROLLBACK-RESTORES-SECRET` — the cleared machine-scope password is put back with its
  original value.
- `UPGRADE-ROLLBACK-RESTORES-SERVICE` — service `Running` again, `certs\` restored with its 3 files.

**Successful upgrade** against the real candidate:

- `UPGRADE-PASS` — result `PASS`, service `Running`, `sourceCommit=19ce7db7…`, `transport=plaintext`.
- `UPGRADE-CERT-DIRECTORY-REMOVED` — reported removed **and** absent on readback. The reported field
  alone is the script talking about itself.
- `UPGRADE-MACHINE-PASSWORD-CLEARED` — the probe variable is gone **and** the production variable
  still holds its original value. Both halves matter: clearing everything that looks like a
  certificate password would satisfy the first half alone.
- `UPGRADE-CONFIG-MIGRATED` — all four removed keys gone, `Health:url` rewritten to
  `http://localhost:58607`. The control is `LEGACY-INSTALL`, where the same detector found all four
  in the same file before the upgrade.
- `UPGRADE-LIVE-OVER-HTTP` — `{"status":"live"}` / 200 at the migrated URL, on the port that spoke
  HTTPS an hour earlier.
- `UPGRADE-NO-KEY-MATERIAL-LEFT` — 0 under the upgraded install and data roots, while the same
  detector still finds the legacy material under the backup root that the rollback depends on.
- `UPGRADE-CURRENTUSER-ROOT-IS-MANUAL` — `currentUserRootCertificateRemoved: false`,
  `currentUserRootCertificateRemovalIsManual: true`. Section 4.5 carries the manual removal;
  reporting it as cleaned here would be a false green.
- `REHEARSAL-CERT-STORES-UNCHANGED` — 0 store changes across the whole rehearsal, including the
  legacy installer's own certificate generation (it creates root and leaf in `CurrentUser\My` and
  deletes both after export; `-InstallCurrentUserRoot` was deliberately not passed).
- `REHEARSAL-PRODUCTION-UNAFFECTED` — 0 drift, password unchanged. This assertion would have failed
  had the rehearsal used the production variable name.

## Red side

`Ticket09Detectors.psm1` holds every detector the two runs depend on, and
`Invoke-Ticket09RedSide.ps1` imports **that same module** rather than a copy of it. Each detector is
shown green on an untouched input and red on a **single-field** mutation of it:

| Detector | Mutation | Red |
| --- | --- | --- |
| `Compare-StoreDigests` | one of 44 thumbprints replaced, count left identical | 1 change (a count-based detector would have seen nothing) |
| `Get-RemovedCertificateKeys` | one key added: `OnboardSafetyProjection:requireHttps` | exactly `requireHttps` |
| `Get-OnboardTlsKeys` | one key added: `wireToGate:useTls` | exactly `useTls` |
| `Get-KeyMaterialFiles` | one file added: `localhost.pfx` | exactly that file |
| `Compare-TreeHashes` | one byte appended to `file2.txt` | exactly `changed:file2.txt` |
| `Get-HashMismatches` | one byte flipped in `RELEASE-CANDIDATE.md` | that file reported |
| `Compare-ProductionSnapshot` | service PID incremented by one | exactly the `pid` field |

## Two harness defects the red side caught, and three the first run caught

The red side's first execution reported 2 of 7 detectors broken, and both were the same family the
map already carries:

1. `@(<empty pipeline>.FullName)` is `@($null)`, whose `Count` is **1**. `Get-KeyMaterialFiles`
   therefore reported one nameless finding when there was nothing to find. Fixed by keeping the
   projection in the pipeline (`| ForEach-Object { $_.FullName }`).
2. `$list.Add('{0}…{4}' -f $a, $b, $c, $d, $e)` — inside a **method-call argument list** the commas
   bind to the call, not to `-f`, so the format operator receives a single argument, throws on
   `{1}`, and the `Add` never runs. `Compare-StoreDigests` could not report a difference at all.
   Fixed by building the string into a variable first.

Round 1 of the acceptance then reported 5 FAIL, of which 3 were further harness defects
(`round1-harness-defects/assertions.json` is kept as their record):

3. the key-material scan was pointed at the whole run root, so it found the `.pfx` the harness itself
   plants as the control for `RC-NO-KEY-MATERIAL`;
4. `@(@(Select-String …).Matches).Count` — the same `@($null)` trap a third time — reported one
   surviving `REPLACE_` placeholder in a file that had none, a **false red**;
5. `Add-Type -Path` against the install root locked `e_sqlite3.dll`, so the uninstall at the end of
   the run could not delete the directory it had just emptied. Now loaded from the release package.

The remaining 2 of the 5 were the missing MesIngest precondition, below.

## Preconditions and environment

- MesIngest was down when round 1 ran: the `MesIngest` service was `Stopped` with a port-less
  `MesIngest.Host.exe` left over, and the ControlServer failed closed exactly as designed —
  `MesIngest catalog polling failed closed; no journey was accepted.` The user authorized clearing
  the leftover process and starting the service; `preconditions/mesingest-precondition.json` records
  the before/after and a contract probe judged by a returned body, never by a connect (the
  workstation's global proxy makes a bare connect succeed against hosts that do not exist).
- The production service was `Running` on PID 8632 throughout all three rounds and both runs, on the
  same three listeners, with its machine-scope certificate password unchanged.
- The onboard repository received zero writes. The onboard binary is the packaged one from the
  candidate; its identity comes from `release-manifest.json`.
