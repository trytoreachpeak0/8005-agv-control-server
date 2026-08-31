# Release candidate rebuilt without the certificate machinery (rc 0.2.0)

Date: 2026-09-01
Command: `scripts/New-WireToGateReleaseCandidate.ps1 -OutputRoot C:\Users\szy\Desktop\w2g-rc-20260901-238b46e -OnboardCommit 238b46eb2c9ae90584e4288a782176f66b7de942`
Full stdout: `build-run.log`

## Why this rebuild exists

`w2g-mvp-rc-0.1.1` binds the TLS-era pair (`ControlServer_MVP@9daeef4` + `OnboardHmi_MVP@31263b1`).
Both ends have since moved to plaintext NDJSON over TCP and plaintext HTTP for the vehicle-safety
projection, and the entire certificate machinery has been removed from the product code, the
install chain and the release manual. This is the candidate built from that pair.

This ticket assembles the candidate and freezes its identity. It does **not** install it, start it
or run a journey against it — that is the clean-install acceptance and the field closed loop.

## Frozen decisions

| Decision | Value | Rationale |
| --- | --- | --- |
| Candidate version | **0.2.0** (`w2g-mvp-rc-0.2.0`) | `0.1.0 -> 0.1.1` moved only the onboard end and stayed interoperable. This one is a transport break: the new and old ends cannot complete a handshake in either direction, and a 0.1.1 site configuration is now *explicitly rejected* at startup by both ends. A patch bump would read as a drop-in replacement. |
| `0.1.0` / `0.1.1` | Kept, immutable | Their assets and tags remain the record of what was qualified. The 0.1.1 release notes get a back-reference to this candidate so an operator who installs 0.1.1 learns why it cannot talk to a new server. That edit belongs to the publish step, not here. |
| Onboard test state at `238b46e` | Measured, not assumed | Recorded as unknown up to this point (no CI in that repository, no evidence attached to the commit, no written answer from its owner). Run here in the throwaway clone instead — see section 6. |

Note for whoever writes the tag: the onboard assembly's own `InformationalVersion` already reads
`0.2.0-safety-mock-travel+<commit>`. That is that application's private version and has nothing to
do with the joint candidate number; the two 0.2.0s are unrelated and must not be conflated.

## Identity, read back from the artifact

| Field | Value | Source |
| --- | --- | --- |
| ControlServer commit | `56d4b1cc2f26325ca853acc4e7278bbde8651874` | `release-manifest.json` |
| ControlServer branch | `ControlServer_MVP` | `release-manifest.json` |
| OnboardHmi commit | `238b46eb2c9ae90584e4288a782176f66b7de942` | `release-manifest.json`, resolved inside the throwaway clone |
| Protocol | `protocol-v0.1.1` @ `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | `controlserver/appsettings.json:ProtocolCandidate` |
| Release manifest SHA-256 | `bfc1d7d433d4f25f0bc7653ccc9b3b2beea04a8390d04132f2299c4ed493346a` | script output |
| SHA256SUMS SHA-256 | `6bb268c9860ac16982e9c02152fe51962f4009ef92d4379aff095e529a0f6549` | script output |

The archived copies under `release-artifacts/` were re-hashed after copying and still equal the two
root hashes above. `.gitattributes` already marks `evidence/rc/*/release-artifacts/**` as `-text`,
so a checkout gets them byte-for-byte rather than line-ending-normalised.

All nine protocol identity fields (`tag`, `commit`, `releaseVersion`, `manifestSha256`,
`schemaBundleSha256`, `vectorsSha256`, `profileId`, `protocolVersion`, `approvalStatus`) are
byte-identical to the 0.1.1 manifest. The protocol repository was not touched this round, so that
is the expected result — a difference would have meant something else moved and would have had to
be explained, not recorded.

## Observations

### 1. The build gates hold, and the scan gate was not loosened

The run exited 0, which the script only does when both ends publish at 0 warnings and the scan gate
passes:

```
Secret scan findings: 0; key material files: 0
Scan gate: PASS (allowlisted unresolved licenses: riot.sdk.core, riot.sdk.facade, riot.sdk.generated)
```

`buildWarnings` is 0 for both components in the manifest, `secretScanFindingCount` and
`secretScanKeyMaterialFileCount` are both 0, and `unexpectedUnresolvedLicenses` is empty.

Removing the certificate machinery did **not** relax the gate. `git diff 9daeef4 56d4b1c --
scripts/New-WireToGateReleaseCandidate.ps1` is empty: the release script — `Assert-ReleaseScanGate`,
the six secret rules, the six key-material extensions and the three-package allowlist — is
byte-identical to the one that produced 0.1.1. The allowlist in this manifest still reads exactly
`riot.sdk.core, riot.sdk.facade, riot.sdk.generated`, the same three named first-party RIoT SDK
packages as in the 0.1.1 manifest.

### 2. File-level delta against the 0.1.1 artifact

`artifact-delta/artifact-delta.json`, computed from the two manifests.

| Component | Same | Changed | Added | Removed |
| --- | --- | --- | --- | --- |
| `controlserver/` | 372 | 11 | 0 | 0 |
| `onboard-hmi/` | 464 | 13 | 0 | 0 |

Both packages keep the same file count as 0.1.1 (383 and 477). The changed entries are the four
ControlServer assemblies and their pdbs, the five onboard assemblies and their pdbs, the two
`appsettings.json`, `appsettings.Production.template.json`, `deployment-manifest.json`, and — new
this round — the two apphost `.exe` files.

The apphosts are worth a line because they are not compiled from this source. Both are byte-for-byte
identical to the 0.1.1 copies except for one 79-byte region (39 and 40 differing bytes, at every
other offset), which is the UTF-16 `InformationalVersion` string inside the Win32 version resource:

| apphost | 0.1.1 | this candidate |
| --- | --- | --- |
| `controlserver/ControlServer.Host.exe` | `1.0.0+9daeef4…` | `1.0.0+56d4b1c…` |
| `onboard-hmi/SQCD.Agv.Wpf.exe` | `0.2.0-safety-mock-travel+31263b1…` | `0.2.0-safety-mock-travel+238b46e…` |

The managed assembly hashes, by contrast, prove nothing: issue 25 established that managed builds
here get a fresh MVID per build, so a DIFF row on a `.dll` is rebuild noise. The content claim is
carried by section 3, not by any hash.

### 3. The certificate machinery is provably gone from the shipped bytes

`symbol-probe/Invoke-SymbolProbe.ps1`, results in `symbol-probe/symbol-probe.json`. Method,
property and type names are written verbatim as UTF-8 into the assembly metadata string heap, so
searching the shipped bytes for a name is a direct content check. Every `.dll` and `.exe` in each
component directory is scanned (373 and 468 files per package); matching is a case-sensitive
ordinal search over the raw bytes, which cannot alias a UTF-16 user-string literal such as
`"serverCertificatePath"`.

| Symbol | 0.1.1 artifact | this artifact |
| --- | --- | --- |
| `OnboardTlsCertificateLoader` (server, removed) | PRESENT | **ABSENT** |
| `CreateTransportStreamAsync` (server, removed) | PRESENT | **ABSENT** |
| `AllowInsecureLoopback` (server, removed) | PRESENT | **ABSENT** |
| `ServerCertificatePasswordEnvironmentVariable` (server, removed) | PRESENT | **ABSENT** |
| `RequireHttps` (server, removed) | PRESENT | **ABSENT** |
| `ValidateServerCertificate` (onboard, removed) | PRESENT | **ABSENT** |
| `CreateTrustedHttpClient` (onboard, removed) | PRESENT | **ABSENT** |
| `ServerCertificateSha256` (onboard, removed) | PRESENT | **ABSENT** |
| `UseTls` (onboard, removed) | PRESENT | **ABSENT** |
| `CreateTransportStreamAsync` (onboard, removed) | PRESENT | **ABSENT** |
| `OnboardTransportOptionsValidator` (server, added) | ABSENT | **PRESENT** |
| `RejectRemovedTransportKeys` (onboard, added) | ABSENT | **PRESENT** |
| `OnboardTcpServer` (server, control) | PRESENT | PRESENT |
| `HandleClientAsync` (server, control) | PRESENT | PRESENT |
| `WireToGateSessionClient` (onboard, control) | PRESENT | PRESENT |
| `ControlServerVehicleSafetySignalProvider` (onboard, control) | PRESENT | PRESENT |

Why the ABSENTs are real negatives: the same reader, over the same file layout, returns PRESENT for
the four control symbols in **both** packages, and returns PRESENT in the new package for the two
symbols that this change *added* — so it can find symbols in this artifact, and it can find them in
the old one. The detector is shown to answer in both directions rather than being uniformly silent.

Verdict is judged over the two products' own assemblies. One name has a framework carrier that must
be named rather than hidden: `RequireHttps` also appears in `Microsoft.AspNetCore.Mvc.Core.dll`
(ASP.NET Core's own `RequireHttpsAttribute`), which ships unchanged in both packages. In
`ControlServer.Host.dll` — this product's code — it went PRESENT to ABSENT. `symbol-probe.json`
records both the product-level and the whole-package state for every probe.

An earlier version of this probe reported all sixteen symbols PRESENT in both packages, including
symbols that only exist in the new one. That was a broken detector, not a finding:
`@($hashtable[$missingKey])` is `@($null)` in PowerShell, whose `Count` is 1, so a miss read as a
hit. The bug is fixed in the archived script and called out in its comments; the table above is
from the fixed run, and the impossible result is what exposed it.

### 4. The operator-facing configuration no longer mentions certificates

The removals reach the two files a site actually edits, not just the binaries:

- `controlserver/appsettings.json` loses `serverCertificatePath`,
  `serverCertificatePasswordEnvironmentVariable`, `allowInsecureLoopback` and
  `OnboardSafetyProjection.requireHttps`;
- `onboard-hmi/appsettings.Production.template.json` loses `useTls` and `serverCertificateSha256`,
  and its projection endpoint moves from `https://REPLACE_CONTROL_SERVER_HOST/...` to `http://...`.

The manifest's `remainingSitePlaceholders` records the consequence: `REPLACE_WITH_64_CHARACTER_SHA256`
is gone from the list an operator has to fill in, leaving six placeholders where 0.1.1 had seven.

### 5. All 868 recorded hashes verify against the bytes on disk

`hash-verify/hash-verify.json`: `recordedFileCount=868`, `ok=868`, `mismatch=[]`, `missing=[]`,
`unlistedFilesInReleaseRoot=[]` — so nothing in the release root is unhashed payload either.

Proven to go red: `controlserver/appsettings.json` was copied **outside** the release root and one
byte appended to the copy. The same comparison reports `OK` on the copy before the mutation and
`MISMATCH` after it (`e6557aad…` -> `8a3ee06c…`). The artifact itself was never modified, and
re-hashing it afterwards still equals the recorded value (`artifactUnchangedAfterRedSide=true`).

### 6. Onboard tests at `238b46e`: 113 passed, 0 failed, 0 skipped

This was the open unknown carried since the read-only verification of the owner's commit: that
repository has no CI, the commit carries no evidence, and its owner answered with a commit rather
than words. It is now measured rather than assumed.

The tests were run inside the throwaway clone the release script had already made at
`w2g-rc-20260901-238b46e-onboard-src` — the exact source tree that produced the shipped binaries.

| Project | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| `SQCD.Agv.UnitTests` (net8.0) | 89 | 0 | 0 |
| `SQCD.Agv.WireToGateG2Tests` (net8.0-windows) | 24 | 0 | 0 |

Raw output in `onboard-tests/run.log`, per-test results in the two `.trx` files.

Writes to the read-only onboard repository remain zero. Its worktree at
`C:\Users\szy\Desktop\8005-agv-onboard-hmi` reports an empty `git status --porcelain`, still on
`OnboardHmi_MVP` at `bbfbc52`, untouched by this ticket. The disposable clone shows zero changes to
tracked files; its only untracked entry is the `global.json` the release script writes to pin the
SDK.

### 7. W2G-IS-00 coverage differs from 0.1.1 by design, not by decay

`OnboardTlsCertificateLoaderTests.cs` existed at `9daeef4` and is deleted at `56d4b1c`. With it
gone, **no test in this repository touches Schannel** — `git grep -niE
'schannel|sslstream|x509|tls'` over `tests/` returns no transport-security test. The two remaining
certificate mentions are the opposite of coverage loss: they are the new negative tests
(`OnboardVehicleSafetyEndpointsTests.RemovedCertificateKeysAreRejectedInsteadOfSilentlyIgnored`,
`EnabledProjectionNoLongerDependsOnCertificateOrHttpsConfiguration`) that assert a removed key now
*fails* startup.

The W2G-IS-00 slice therefore cannot be compared case-by-case with the 0.1.1 evidence. That is the
necessary consequence of deleting the mechanism, not a silent drop in coverage: there is no longer
a TLS code path for a test to cover.

### 8. No tier 1 run was made for this ticket, and none was due

`git diff ae4a17d 56d4b1c -- src/ tests/` is empty. The last tier 1 run (249 passed, 0 skipped) was
made at `ae4a17d`, and every commit since — the install-chain strip, the G3 runner alignment, the
cross-machine evidence and the manual rewrite — changed only scripts, documentation and evidence.
The candidate's product source is exactly the source that run covered.

## What this candidate is not yet qualified for

Nothing in this ticket started the artifact. Clean-install acceptance, the field closed loop, and
publication are separate steps, and the known limitations of the plaintext form (credential proof
in the clear, a tamperable safety gate input, no interoperability with 0.1.x ends) must appear in
the release notes when it is published.
