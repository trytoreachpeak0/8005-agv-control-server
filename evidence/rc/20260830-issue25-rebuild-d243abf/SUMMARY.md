# Release candidate rebuilt on the fixed commit (issue 25)

Date: 2026-08-30
Command: `scripts/New-WireToGateReleaseCandidate.ps1 -OutputRoot C:\Users\szy\Desktop\w2g-rc-20260830-d243abf -OnboardCommit 304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6`
Full stdout: `build-run.log`

## Why this rebuild exists

The release candidate delivered by issue 11 binds `ControlServer_MVP@2eeb6f0`. Seven commits
landed after it, and `127b137` changed product code:

```
127b137  src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs   47 +, 1 -
```

That is the issue 22 fix which carries snapshot revisions across journeys on one vehicle.
Without it the second journey on the same AGV re-publishes a revision the onboard peer has
already adopted and is refused as `SNAPSHOT_REVISION_REGRESSION`, which tears the session down.
`1fd23ac` additionally turned the release scan results into `Assert-ReleaseScanGate` (issue 24),
so the `2eeb6f0` artifact was also assembled by a script that could not fail on its own findings.

Qualifying a deployment against the `2eeb6f0` artifact would therefore qualify a binary with a
known second-journey defect.

## Identity, read back from the artifact

| Field | Value | Source |
| --- | --- | --- |
| ControlServer commit | `d243abfc48fe7229f2f8e29374e20b930dd4c8d3` | `release-manifest.json` |
| OnboardHmi commit | `304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6` | `release-manifest.json` |
| Protocol | `protocol-v0.1.1` @ `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | `controlserver/appsettings.json:ProtocolCandidate` |
| Release manifest SHA-256 | `d680ca2adef2ec920ed5f9944591b593d3535cd9793673c8375291a64f3009d9` | recomputed from the archived copy |
| SHA256SUMS SHA-256 | `e195f8fd74d995447b6ea3df0e011ee88e43735fc25a44aeb92db6f0585aafc7` | recomputed from the archived copy |

Every protocol field (`tag`, `commit`, `releaseVersion`, `manifestSha256`, `schemaBundleSha256`,
`vectorsSha256`, `profileId`, `approvalStatus`, `identitySource`) is byte-identical to the
`2eeb6f0` artifact's manifest. The protocol side did not move; only the server did.

## Observations

### 1. The build gates hold

`New-WireToGateReleaseCandidate.ps1` throws on any publish warning (lines 257-259, 290-292) and
on any scan finding. The run exited 0, so both ends published at 0 warnings, and the gate
reported `PASS` with the three named allowlisted RIoT SDK packages and nothing else:

```
Secret scan findings: 0; key material files: 0
Scan gate: PASS (allowlisted unresolved licenses: riot.sdk.core, riot.sdk.facade, riot.sdk.generated)
```

### 2. Assembly hashes cannot prove the fix shipped — the control disproved that check

The first check tried was "the rebuilt `ControlServer.Infrastructure.dll` must hash differently
from the `2eeb6f0` one". Its control is the onboard assembly, built from the *same* commit
`304e6ad` with the same SDK, which should therefore be identical:

| Artifact | `2eeb6f0` vs `d243abf` |
| --- | --- |
| `controlserver/ControlServer.Infrastructure.dll` | DIFF |
| `controlserver/ControlServer.Application.dll` | DIFF |
| `controlserver/ControlServer.Domain.dll` | DIFF |
| `controlserver/ControlServer.Host.dll` | DIFF |
| `onboard-hmi/SQCD.Agv.Wpf.dll` (same source) | **DIFF** |
| `onboard-hmi/SQCD.Agv.Wpf.exe` (apphost) | SAME |

The onboard managed assembly differs from an identical source tree, so managed builds here are
not reproducible (a fresh MVID per build). A differing hash therefore proves nothing about
content, and this check was discarded rather than reported as evidence.

### 3. The fix is provably in the shipped bytes

`127b137` introduces a new private method `SeedSnapshotRevisionsAsync`, whose name is written to
the assembly metadata string heap. Searching the shipped assemblies for that name is a direct
byte-level check, with an existing method as the detector control:

| Symbol | `2eeb6f0` artifact | `d243abf` artifact |
| --- | --- | --- |
| `SeedSnapshotRevisionsAsync` (added by `127b137`) | ABSENT | **PRESENT** |
| `GetNextSessionGenerationAsync` (pre-existing) | PRESENT | PRESENT |

The red side is the first row's left cell: the same search over the same file layout returns
ABSENT for the artifact that lacks the fix, so the search is not silently matching everything.
The second row shows the search does find symbols in the old artifact, so ABSENT there is a real
negative and not a broken reader.

This is a metadata-level proof that the compiled fix is present. It is **not** a behavioural
proof: no journey was run against this artifact. The behavioural evidence for the fix itself is
the tier 1 run at `d243abf` (243 passed / 0 skipped, including the `JourneyRuntimeWorkerTests`
assertions added by `127b137` and shown red under their own mutation).

### 4. All 868 recorded hashes verify against the bytes on disk

`sha256sum -c` over the full `SHA256SUMS.txt`: 868/868 OK.

Proven to go red: a byte appended to a **copy** of `controlserver/appsettings.json` outside the
artifact turns that same one-line check from GREEN to RED. The artifact itself was not modified.

The archived copies of `release-manifest.json` and `SHA256SUMS.txt` were hashed after copying and
still equal the two root hashes above, before being committed.

## What this does not establish

- No installation, service start, session, or journey was run against this artifact. Isolated
  install verification is issue 13 / 12.
- The onboard default settings still declare a stale build commit —
  `components.onboardHmi.configuration.declaredBuildCommitInDefaultSettings` is
  `a6f05fbced15316a2cc20cd327f80c5c5ee1821e`, not `304e6ad`. This is the already-reported onboard
  finding, owned by the read-only onboard repository, and is unchanged by this rebuild.
- The `2eeb6f0` artifact was **not** deleted. Both roots exist side by side under
  `C:\Users\szy\Desktop\`.
- The throwaway onboard clone `C:\Users\szy\Desktop\w2g-rc-20260830-d243abf-onboard-src`
  (167 MB) is left in place; it is reproducible and safe to delete.
- The onboard repository was not written to. Its local clone is still at `bbfbc52` with a clean
  working tree; the script builds from its own one-shot clone.
