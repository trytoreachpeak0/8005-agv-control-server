# Release candidate rebuilt on the fixed onboard commit (rc 0.1.1)

Date: 2026-08-31
Command: `scripts/New-WireToGateReleaseCandidate.ps1 -OutputRoot C:\Users\szy\Desktop\w2g-rc-20260831-31263b1 -OnboardCommit 31263b1ffd372db1f27af5e1143ebad7e7679715`
Full stdout: `build-run.log`

## Why this rebuild exists

`w2g-mvp-rc-0.1.0` binds `OnboardHmi_MVP@304e6ad`, whose HMI keeps its status banner pinned to
`Connecting` because `DisabledRuleGateway.IsConnected` is always false. The WIRE_TO_GATE business
therefore never reaches the banner or the operator log — known limitation 1a of the 0.1.0 release
notes, which states that the fix exists at `OnboardHmi_MVP@31263b1` but is **not** in that asset,
and that having it requires rebuilding the candidate. This is that rebuild.

The ControlServer side did not move: both candidates are built from `ControlServer_MVP@9daeef4`.

## Identity, read back from the artifact

| Field | Value | Source |
| --- | --- | --- |
| ControlServer commit | `9daeef4325fccf094767b689fa484d8e1e414042` | `release-manifest.json` |
| OnboardHmi commit | `31263b1ffd372db1f27af5e1143ebad7e7679715` | `release-manifest.json` |
| Protocol | `protocol-v0.1.1` @ `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | `controlserver/appsettings.json:ProtocolCandidate` |
| Release manifest SHA-256 | `12a7ce56ff1f74eb6a15e15bc1947240876262c4d457c1b1e3573ce04912578b` | recomputed from the archived copy |
| SHA256SUMS SHA-256 | `e607a9d9adedcf91e13b832e868d78273137e69af7098c8fd828c21c35384847` | recomputed from the archived copy |

All four protocol identity fields (`repositoryCommit`, `manifestSha256`, `schemaBundleSha256`,
`vectorsSha256`) are byte-identical to the 0.1.0 manifest. Only the onboard end moved.

## Observations

### 1. The build gates hold

The run exited 0, so both ends published at 0 warnings, and the scan gate reported `PASS`:

```
Secret scan findings: 0; key material files: 0
Scan gate: PASS (allowlisted unresolved licenses: riot.sdk.core, riot.sdk.facade, riot.sdk.generated)
```

### 2. File-level delta against the 0.1.0 artifact

| Component | Same | Changed | Added | Removed |
| --- | --- | --- | --- | --- |
| `controlserver/` | 373 | 10 | 0 | 0 |
| `onboard-hmi/` | 465 | 12 | 0 | 0 |

The ten ControlServer entries are managed assemblies, their pdbs and `deployment-manifest.json`,
from the same commit — rebuild noise, not content. Issue 25 already established that managed
builds here are not reproducible (fresh MVID per build), so a differing assembly hash proves
nothing either way. The twelfth onboard entry is `appsettings.Production.template.json`, whose
`onboardBuildCommit` the release script stamped with this package's real commit.

### 3. The fix is provably in the shipped bytes

Four symbols introduced by `31263b1` are written to the assembly metadata string heap. Two
pre-existing symbols act as the detector control:

| Symbol | 0.1.0 artifact (`304e6ad`) | this artifact (`31263b1`) |
| --- | --- | --- |
| `ApplyWireToGatePresentationCore` (added) | ABSENT | **PRESENT** |
| `ApplyWireToGateOperatorEvent` (added) | ABSENT | **PRESENT** |
| `MatchesPersistedResumeState` (added) | ABSENT | **PRESENT** |
| `WireToGateHmiBanner` (added) | ABSENT | **PRESENT** |
| `HandleBlockedResumeAsync` (pre-existing) | PRESENT | PRESENT |
| `SubmitSublotAsync` (pre-existing) | PRESENT | PRESENT |

The red side is the left column of the first four rows: the same search over the same file layout
returns ABSENT for the artifact that lacks the fix. The last two rows show the search does find
symbols in that old artifact, so those ABSENTs are real negatives, not a broken reader. The four
added symbols are also absent from the `304e6ad` **source** tree (`git grep` matches 0 files), so
they are additions rather than renames.

### 4. All 868 recorded hashes verify against the bytes on disk

`OK=868 MISMATCH=0 MISSING=0` over the full `SHA256SUMS.txt`.

Proven to go red: a byte appended to a **copy** of `controlserver/appsettings.json` outside the
artifact turns that same one-line check from GREEN to RED. The artifact itself was not modified,
and re-hashing it afterwards still matches the manifest value.

### 5. Isolated install and uninstall both PASS

Installed as a fully isolated instance — service `8005 AGV ControlServer Rc011`, ports 58605 /
58607, own install root, data root and backup root, `-SkipMachineEnvironmentInjection` — so the
production service was never touched. `install/result.json`: `result=PASS`, all nine checks,
`databaseCreatedByMigration=true`, `journeyRuntimeEnabled=false`, `riotMutationPerformed=false`,
`orderCreated=false`, `vehicleMoved=false`.

`install/uninstall.json`: `result=PASS`, service removed, install root and data root removed,
`trustedRootCertificatesRemoved=1`, and `productionServicePresent=true` / `Running` throughout.
Verified afterwards: ports 58605 and 58607 no longer listening, the pinned root no longer in
`CurrentUser\Root`, production service still Running.

### 6. HMI banner: a red/green comparison at one server state

Both onboard builds were run against the same isolated instance, with configurations that differ
only in their declared `onboardBuildCommit` and journal path. The server judged
`readiness=RecoveryRequired` on both runs.

| UI element | 0.1.0 onboard (`304e6ad`) | this artifact (`31263b1`) |
| --- | --- | --- |
| Status banner | 「连接中 — 正在等待仓门控制设备和任务系统连接…」 | 「需要恢复 — 上层会话要求恢复：`DEPARTURE_SAFETY_NOT_READY`。请保持车辆停稳，禁止重复操作仓门。」 |
| Header 「任务系统」 | 离线 | 在线 |
| Header 「上层会话」 | 需恢复 | 需恢复 (same) |

Screenshots: `hmi-compare/old-hmi.png`, `hmi-compare/new-hmi.png`. Logs
(`hmi-compare/*-agv-*.log`) show both builds establishing a session at the same readiness, with
`version=…+<commit>` landing on their respective commits — same session state, different binary,
so the visible difference can only come from the fix.

### 7. End-to-end field closed loop, re-run on this artifact (generation 8)

Run kind `TICKET14_FIELD_CLOSED_LOOP_ON_RELEASE_CANDIDATE`, driven by the ticket 14 scripts with
`-ReleaseRoot` pointed at this candidate. User gave the on-site physical safety GO, operator
`S0020310`, `dispatchGeneration=8`. Evidence: `field-closed-loop-gen8/`.

`run-result.json`: `journeyStage=Completed`, `blockReasonCode` empty, `completed=true`,
`SessionGeneration=1` throughout, `hostStderrEmpty=true`, `portsReleased=true`,
`trustRemaining=0`. Demand `18b7d18f-…`, pickup `N2-4_N3-4` (20), gate `关卡` (210), one basket,
08:43:15Z → 08:52:54Z.

Two real RIoT orders, each created once, five-phase audit complete on both
(`PRE_CREATE_RECONCILIATION(UNKNOWN/AbsentAtObservation) → CREATE_DISPATCH(ARMED) →
CREATE_REQUEST(STARTED) → CREATE_RESPONSE(ACCEPTED/SdkAccepted) →
POST_CREATE_RECONCILIATION(CONFIRMED/Found)`):

| UpperId | Purpose | Target | OrderId |
| --- | --- | --- | --- |
| `W2G-18b7d18f-…-PICKUP-8` | `TO_PICKUP` | `N2-4_N3-4` / 20 | `order-2094345282652340224` |
| `W2G-18b7d18f-…-GATE-8` | `TO_GATE` | `关卡` / 210 | `order-2094346185258172416` |

Terminal counts: `AcceptedDemands=1`, `JourneyRuntimes=1`, `OrderIntents=2`,
`RiotDispatchAuditEvents=10`, `StationOperations=2` (`Load` and `Unload` both `Committed`,
sublot `Q26085155-5`), `OperationResults=2`, `ProtocolInbox=171`, `ProtocolOutbox=10`.

**This closes the operator-log gap left by section 6.** Four screenshots across the run show the
HMI tracking the business throughout, which the `304e6ad` build could not do:

| Screenshot | Stage | Banner | Operator log |
| --- | --- | --- | --- |
| `hmi-01-moving.png` | `AwaitingPickupArrival` | 需要恢复 / `DEPARTURE_SAFETY_NOT_READY` | startup only |
| `hmi-02-awaiting-sublot.png` | `AwaitingSublot` | 可扫码，已到站 | `收到子批录入请求：Q26085155-5。` |
| `hmi-03-to-gate.png` | `AwaitingGateArrival` | 需要恢复 / `DEPARTURE_SAFETY_NOT_READY` | slot 1 operation complete → result confirmed by server → **repeated slot command held to the original result without re-driving door IO** |
| `hmi-04-final.png` | `Completed` | 就绪 | unload projected the same way |

The header also tracked the journey: 「到站」 went from 未到站 to `N2-4_N3-4 / Q26085155-5` to
`关卡 / Q26085155-5`, and 「发车安全」 alternated 禁止发车 / 允许发车 with the safety gate — the same
gate behaviour ticket 14 recorded, now visible to the operator instead of hidden behind a
`Connecting` banner.

The IO simulator assist (`load-assist.log`) is the gen7 single-decision version and behaved
correctly: one door, plan decided once on open, closed in 1.6 s.

## What this does not establish

- **The IO is still the simulator, not real eight-slot IO**, and the onboard ran on a development
  workstation rather than the target terminal. Known limitation 2 is unchanged.
- Known limitation 3 of the 0.1.0 notes (server correctness depends on the peer re-ACKing repeated
  snapshots) was **not** re-checked against `31263b1`.
- **New cosmetic finding, owned by the read-only onboard repository:** the operator log mixes two
  time zones in one panel. The 「系统」 startup lines are stamped in local time (`16:43:12`) while
  every 「操作」/「成功」 line added by `31263b1` is stamped in UTC (`08:45:21`, `08:46:49`,
  `08:52:54`), so the business entries read eight hours earlier than the startup entries above
  them. Visible in all four screenshots. Not a blocker and not routed anywhere in this repository.
- The onboard default `appsettings.json` still declares a stale build commit
  (`declaredBuildCommitInDefaultSettings=a6f05fbced15316a2cc20cd327f80c5c5ee1821e`,
  `declaredBuildCommitMatchesBuild=false`). It is owned by the read-only onboard repository. The
  shipped `appsettings.Production.template.json` does carry the correct `31263b1…`.
- The onboard repository was **not written to**: only fetched and read; the release script builds
  from its own one-shot clone (`C:\Users\szy\Desktop\w2g-rc-20260831-31263b1-onboard-src`, safe to
  delete). Its local clone is still at `bbfbc52` with a clean working tree.
- The 0.1.0 artifact was not deleted or modified; its `SHA256SUMS.txt` entry for the file used in
  the red-side check still verifies.
