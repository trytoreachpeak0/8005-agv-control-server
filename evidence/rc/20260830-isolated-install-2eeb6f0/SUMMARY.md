# Release candidate build and isolated install verification (`2eeb6f0`)

Ticket 11 of the WIRE_TO_GATE delivery map. The release candidate was assembled from a clean
ControlServer worktree and a throwaway clone of the read-only onboard repository, then installed,
started, stopped, restarted and rolled back as an **isolated second instance** on this machine
while the production service kept running untouched.

## Bound identity

| Component | Identity |
| --- | --- |
| ControlServer | `ControlServer_MVP@2eeb6f009354d34800c5c024b58e75364c8ee5da` |
| OnboardHmi | `OnboardHmi_MVP@304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6` (read-only for agents; built from a throwaway clone) |
| Protocol | `protocol-v0.1.1` / `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`, `APPROVED_RELEASE` |
| Release manifest SHA-256 | `97468caec7630cbe7f9e4bbe37fdafd098bde70bee01b0333e37961b3585aec1` |
| SHA256SUMS SHA-256 | `221ea67ce1b6c4a69c8c64d17b30a57c0a3e47ddc409a6eba3d8a16daf86e3ec` |

The protocol identity is **read back** from the published `controlserver/appsettings.json`, not
restated by the release script, and the script refuses to continue unless `approvalStatus` is
`APPROVED_RELEASE`.

## Isolation from the running production deployment

| Property | Production | Isolated verification instance |
| --- | --- | --- |
| Service | `8005 AGV ControlServer` | `8005 AGV ControlServer RC Verify` (PID 30416, LocalSystem, Auto) |
| Onboard / health port | 58005 / 58007 | 58405 / 58407 |
| Install root | `C:\Program Files\8005 AGV\ControlServer` | `…\ControlServer-RC-Verify` |
| Data root | `C:\ProgramData\8005\ControlServer` | `…\ControlServer-RC-Verify` |
| Machine-scope environment | untouched | not written (`-SkipMachineEnvironmentInjection`) |
| Certificate trust store | untouched | untouched (chain pinned to the install's own PEM) |

## Assertions

| # | Assertion | Result |
| --- | --- | --- |
| 1 | ControlServer worktree clean before the build | PASS |
| 2 | ControlServer publish reports 0 warnings | PASS |
| 3 | OnboardHmi publish reports 0 warnings (`TreatWarningsAsErrors=true`) | PASS |
| 4 | Onboard clone resolves to the requested commit and is clean after checkout | PASS |
| 5 | Protocol identity read back from the package, `APPROVED_RELEASE` | PASS |
| 6 | Release files hash-verified against `SHA256SUMS.txt` | PASS, 868 checked, 0 mismatched |
| 7 | Install verifies every package file against `deployment-manifest.json` | PASS |
| 8 | Service created as `LocalSystem` / `Auto` | PASS |
| 9 | SQLite database created by EF migrations at first start | PASS, 323,584 bytes |
| 10 | `/health/live` after first start | PASS |
| 11 | `/health/ready` reaches the database on first start | PASS, `503 RECOVERY_HANDSHAKE_REQUIRED` |
| 12 | stop → start → `/health/live` | PASS |
| 13 | forced restart → `/health/live` | PASS |
| 14 | `/version` returns the bound protocol identity | PASS, exit 0 with the pinned root |
| 15 | Service writes a persistent log file | PASS, 140 NDJSON lines, 90,644 bytes archived |
| 16 | No secret value appears in the service log | PASS, 0 matches |
| 17 | Machine-scope environment variables not written | PASS, `machineEnvironmentInjected=false` |
| 18 | No certificate added to `CurrentUser\Root` by this run | PASS, 0 with this run's root thumbprint |
| 19 | OnboardHmi executable starts with a responsive main window | PASS, `8005 多仓位AGV车载端` |
| 20 | OnboardHmi writes its own log at `<install>\logs\agv-<date>.log` | PASS, 544 bytes |
| 21 | OnboardHmi binary reports the build commit it was built from | PASS, `version=0.2.0-safety-mock-travel+304e6ad9…` |
| 22 | Uninstall without `-RemoveDataRoot` removes the service and install root, retains the database | PASS |
| 23 | Uninstall with `-RemoveDataRoot` removes service, install root and data root | PASS |
| 24 | Ports 58405 / 58407 released, no stray `ControlServer.Host` or `SQCD.Agv.Wpf` process | PASS |
| 25 | Production service `Running` on 58005 / 58007 before, during and after | PASS |
| 26 | Secret scan over release artifacts, release scripts and both source trees | PASS, 0 findings, 0 key-material files |

## Falsifiability

Green assertions are only evidence once they are shown to be able to turn red.

| Check | Mutation | Outcome |
| --- | --- | --- |
| Secret scan | 6 planted secrets (one per rule) + 6 key-material extensions + 1 clean control | 13/13 detected, 0 false positives |
| `SHA256SUMS.txt` | appended one byte to `RELEASE-CANDIDATE.md` | MISMATCH detected, MATCH again after restore |
| HTTPS chain pin | verified `https://localhost:58407` with the *production* instance's root instead | `curl: (60) untrusted root`, exit 60; exit 0 with the matching root |
| Uninstall production guard | uninstall targeted at the production service without `-AllowProductionService` | refused, production still `Running`, no result file written |
| Install rollback | see `../20260830-install-rollback-under-failure-4c13ea0/` | service, install root and data root all absent after a real failure |

The secret-scan mutation harness reads the rules and the `Invoke-SecretScan` body **out of the
committed release script by AST**, so it exercises the shipped code rather than a copy.

## Defects found and fixed in this run

1. `[IO.Path]::GetFullPath` resolves relative paths against the **process** working directory, not
   the PowerShell location, so every relative path an operator passed after a `Set-Location` was
   silently rebased onto an unrelated directory. Fixed in `4c13ea0` across all four scripts.
2. The install imported its generated root into `CurrentUser\Root`, which raises a Windows trust
   dialog and therefore cannot complete in a non-interactive session — while mutating a store the
   product never consults, since the onboard client pins `serverCertificateSha256`. Fixed in
   `a5698da`: the root is exported as PEM and the chain is verified with `curl --cacert`; the
   trust-store import is now an opt-in switch.
3. The service had no persistent log: Serilog was configured with a Console sink only, which under
   a Windows Service writes nowhere. The install-generated `appsettings.Production.json` now adds
   a Compact-JSON file sink, and the install fails if no log file appears.

## Findings reported, not fixed here

1. **Onboard configuration carries a stale build commit.** `wireToGate.onboardBuildCommit` in the
   shipped default `appsettings.json` is `a6f05fbc…`, an older commit in the same repository, while
   the package is built from `304e6ad…`. The binary itself is correct — its startup log reports
   `version=0.2.0-safety-mock-travel+304e6ad9…` — so this is a configuration default, not a binary
   identity error, and the repository's own `appsettings.Production.example.json` expects the
   operator to replace it. The release script therefore emits
   `onboard-hmi/appsettings.Production.template.json` with the true commit already stamped, and
   records the mismatch in the manifest as
   `components.onboardHmi.configuration.declaredBuildCommitMatchesBuild=false`. **The onboard
   repository is read-only for agents, so the default was not changed there.**
2. **The onboard repository has no `global.json`**, so a clean clone builds with whatever SDK is
   first on the machine. The release script pins `8.0.424` inside its throwaway clone and records
   `sdkPinnedByReleaseScript=true`; pinning it in the repository is the onboard owner's call.
3. **Three first-party packages declare no license metadata**: `RIoT.Sdk.Core`,
   `RIoT.Sdk.Facade`, `RIoT.Sdk.Generated`, all `0.1.0-controlserver.2`. The remaining 104 packages
   across both components resolve to MIT, Apache-2.0, BSD-3-Clause or a dotnet license URL.
4. **An orphaned development root certificate is present in `CurrentUser\Root`**:
   `CN=8005 AGV ControlServer Local Development Root 20260827T075424Z`, thumbprint
   `8CEF9E6B09A7202AB6B8DF5323E6A48E3CE47003`, left by an install run on 2026-08-27. It is not the
   running production instance's root — validating production's endpoint against production's own
   exported root fails as untrusted, so that root is not in the store. Left in place rather than
   removed unilaterally.

## What this does not establish

- **Not another machine.** The user chose an isolated instance on this workstation over the
  Hyper-V VM. Every step was executed from the release output directory using only the scripts and
  the manual copied inside it, never from a repository working copy — but a second physical or
  virtual host was not used, and the operator was the session that authored the manual.
- **Not a passing release candidate.** W2G-IS-00 through W2G-IS-07 and the RC gate remain
  `INCONCLUSIVE`. This run proves the package builds, installs, starts, survives stop/start and
  restart, logs, and rolls back — nothing about business-slice acceptance.
- **No RIoT mutation, no order, no vehicle movement.** `JourneyRuntime.enabled=false` throughout;
  the result JSON records `riotMutationPerformed`, `orderCreated` and `vehicleMoved` as `false`.
- **The core test scenarios were not re-run here.** `docs/RELEASE-CANDIDATE.md` §12 documents their
  entry points; this ticket did not repeat them.

## Files

| File | Content |
| --- | --- |
| `install-result.json` | install result, schema 2, `PASS` |
| `install-diagnostic.log` | per-stage UTC trace of the install |
| `service-log-controlserver-20260830.ndjson` | the service's own log from the verification instance |
| `onboard-agv-20260830.log` | the onboard application's own log from its verification launch |
| `uninstall-retain-data-result.json` | uninstall keeping the data root |
| `uninstall-remove-data-result.json` | uninstall removing the data root |
| `release-artifacts/release-manifest.json` | the joint release manifest this run produced |
| `release-artifacts/SHA256SUMS.txt` | the 868-file hash list assertion 6 was checked against |
| `release-artifacts/inventory/dependencies-controlserver.json` | 103 packages with resolved licenses |
| `release-artifacts/inventory/dependencies-onboard.json` | 4 packages with resolved licenses |
| `release-artifacts/inventory/secret-scan.json` | the scan behind assertion 26 |

## Archived afterwards (map ticket 24)

`release-artifacts/` was **not** archived when this run happened; only the two root hashes in the
table above were, which left findings #3 and assertion 26 with nothing in the repository to re-check.
The five files were copied out of the release output directory on 2026-08-30 under map ticket 24,
after verifying that `release-manifest.json` and `SHA256SUMS.txt` still hash to the two values
recorded above — so they are this run's own artifacts, not a rebuild.

Assertion 26 was, at the time of this run, a **recorded count and not a gate**: the release script
wrote the finding counts into JSON and packaged regardless. Ticket 24 added
`Assert-ReleaseScanGate`, so a later release with a finding, a key-material file or an unlisted
unresolved license fails instead of shipping. That gate is not retroactive to this package.
