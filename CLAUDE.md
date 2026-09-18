# 8005-agv-control-server

The production server repository for the WIRE_TO_GATE MVP. This file is the
agent's instructions for working here.

## Write authority

This repository is writable. The workspace `CLAUDE.md` is the authority on write
access and wins where this file differs; the two restrictions an agent here is
most likely to meet:

- `8005-agv-onboard-hmi` and `slots-simulator` — **writable on `w2g/*` branches
  only** (changed 2026-09-04; they were read-only for agents before). The
  development work on both is ours. A change reaches their working branches
  (`OnboardHmi_MVP` and `main`) only as a pull request, and **since 2026-09-09
  both the agreement and the merge are ours**. Never push to those branches
  directly, never force-push, never delete or rewrite a branch that is not ours,
  never tag or release there.
- `8005-agv-protocol` — writable, with **no announcement duty since 2026-09-08**:
  do not open announcement issues and do not `@SocialKKKK`. A push still voids
  gate evidence — ours — so its commit message states which `FP-IS-*` slices it
  touches and which evidence it voids.

Reaching a machine, or routing a problem to a repository, never grants write
access to it.

If this repository is cloned on its own — outside the `8005-workspace` workspace
— treat all three as read-only and ask.

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues. See `docs/agents/issue-tracker.md`.
External pull requests are treated as a request surface and run through the same
triage labels — that flag is on.

### Triage labels

The five canonical roles (`needs-triage`, `needs-info`, `ready-for-agent`,
`ready-for-human`, `wontfix`), created in this repository. See
`docs/agents/triage-labels.md`.

### Domain docs

`CONTEXT.md` at the repository root plus `docs/adr/`. See `docs/agents/domain.md`.

### Matt Pocock's skills

Installed as the `mattpocock-skills` plugin (user-level). Invoke them namespaced:
`/mattpocock-skills:<name>`. They are explicit-only — use one when the user names
it. `code-review` collides with the bundled `/code-review`; use
`/mattpocock-skills:code-review` for the Standards+Spec review.

## Collaboration workflow

Zhengyu Shao is the sole developer. Kun Wang (GitHub `SocialKKKK`) no longer
works on the project: development of `8005-agv-onboard-hmi` and `slots-simulator`
moved to us on 2026-09-04, and this repository and `8005-agv-protocol` are ours
too. The workspace `CLAUDE.md` is the authority on these rules; the account
written for humans, in Chinese, is `8005-agv-program/docs/collaboration-workflow.md`.

What an agent must follow:

- **The unit of collaboration is the integration slice.** Do not invent another
  one. `8005-agv-protocol/integration-slices/index.json` defines `FP-IS-00`
  through `FP-IS-15`, each with a `sequence` and `prerequisites`. Each slice's
  `gates` array names who proves what: `G1` the protocol, `CONTROL_SERVER_G2`
  this repository, `ONBOARD_HMI_G2` the onboard HMI (run by us since
  2026-09-04), `G3` both ends together.
  **The family replaced `W2G-IS-00` through `07` rather than joining them**
  (scope specification 7.1); `FP-IS-00` through `07` correspond to the old eight
  one for one, but as *recertification under v2* — there is no passing verdict to
  carry over, and section 12 of `docs/RELEASE-CANDIDATE.md` says so in as many
  words. Existing evidence directories keep the `W2G-IS-NN` ids they were written
  with; nothing renames them.
- **The two G2 gates have no dependency and run in parallel.** G3 has been run by
  one person since 2026-09-08, but it is still the most expensive gate: do not
  propose it while either G2 is not green, and before entering it say what it
  costs and ask.
- **Cross-repository feedback takes one of three routes.** A contract ambiguity
  or error goes to an issue in `8005-agv-protocol` carrying the `vectorId` that
  triggered it. A peer (`8005-agv-onboard-hmi`, `slots-simulator`) failing the
  contract goes to an issue in *that* repository — **run G3 for evidence first**
  and attach the evidence directory.
  Work inside this repository stays in this repository's issues.
  **A cross-repository claim must carry reproducible gate evidence; "it does not
  work on my side" is not a report.** Follow the shape already used in
  `docs/defects/`: a `Found by:` line linking the G3 evidence `SUMMARY.md`.
- **`8005-agv-protocol` needs no advance approval** — Zhengyu Shao decides its
  content alone — **and since 2026-09-08 no announcement either**: do not open
  announcement issues and do not `@SocialKKKK`. A push still voids gate evidence,
  now ours, so its commit message states which `FP-IS-*` slices it touches and
  which evidence it voids. Tagging a release needs an attestation with **exactly
  one approval** (two before 2026-09-08): the product owner, or since 2026-09-12
  an AI agent the user authorized for that specific release, recorded as
  `approverKind: AI_AGENT` with `authorizedBy`. **CI cannot approve.**
- **Batch protocol changes.** A patch release voids the affected G1/G2/G3
  evidence on both ends — all of it ours now — so every small change costs a full
  gate re-run.

## Language

Agent instruction files — this one, and anything under `.claude/` — are written
in **English**.

Everything a human reads is written in **Chinese**: README files, documentation
prose, `docs/defects/` entries, evidence summaries, issue and pull-request
titles and bodies, and commit message bodies.

Stay English inside Chinese text: conventional commit prefixes (`feat:`, `fix:`,
`docs:`, `chore:`), identifiers, paths, commands, environment variables, error
codes, gate and slice names (`G1`, `FP-IS-00`), and protocol message names,
schema fields and `vectorId` values — those are the contract itself. Quote an
error or a test result in its original English first, then explain it in
Chinese. Do not rewrite existing text to match; this governs new writing.

## Tests and gates

**"Run the full test suite" means this one command here**, never a gate:

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```

Historically 583 passed / 0 skipped. Build with the .NET SDK pinned in
`global.json` (`8.0.424`); when it is not on `PATH`, point
`WIRE_TO_GATE_DOTNET_EXE` at that version's `dotnet.exe` and run
`.\scripts\build.ps1`.

**That command also checks every outbound protocol line against the protocol JSON Schema**, and it
can fail while its console summary says `Failed: 0`. When the run ends,
`tests/ControlServer.Tests/OutboundSchemaConformance.cs` hands what the tests sent to
`tools/ControlServer.SchemaConformance` (its own process: its System.Text.Json 10 must not enter the
test host). A violation is reported as `[Test Assembly Cleanup Failure] Xunit.Sdk.TestPipelineException`
with exit code 1; the detail is in the TRX and in `schema-conformance/` next to the test assembly.
Never "fix" such a failure by adding to `tests/ControlServer.Tests/schema-known-violations.json`
without an issue that owns the violation -- that list is for defects already filed, and every entry
names one. It adds about 40 to 50 seconds of Roslyn schema compilation per run (measured 38.6 s and
47.9 s on the two runs that landed control-server#85).

Test authorization is scoped to the current task. A request to inspect, tidy,
commit, or push an already-dirty worktree does **not** authorize a test run. Run
tests only when the current task changed product code, tests, or build inputs,
or when the user asks for validation. Never infer it from `git status`.

Between the suite and the gates sits the **L2 scenario runner**, which is cheap (about 14
seconds), unattended and safe to run on your own initiative:

```powershell
pwsh .\scripts\l2\Invoke-L2Scenario.ps1 -Scenario normal-load -EvidenceRoot .\evidence\l2\<new dir>
```

It starts a real ControlServer against loopback doubles — `tools/ControlServer.FakeRiot`,
`tools/ControlServer.FakeMesIngest` and the synthetic peer in `tools/ControlServer.FakeOnboard` —
drives one scenario, asserts against the server's own database and writes evidence. Use it when a
change touches the journey runtime's cross-end timing, which the unit suite covers only from
inside one process. `-EvidenceRoot` must be a new directory. See `scripts/l2/README.md`; a PASS
there proves nothing about real hardware.

**Every L2 run, on every rig, takes a machine-wide port lock and queues behind any other L2 run.**
All rigs bind the same fixed port block (48405–48416, synthetic peers from 48420), so two runs at
once talk to each other's processes — on 2026-09-14 that sent a G3 run into its scenario with two
dead doubles. `Invoke-L2Scenario.ps1` takes the named mutex `Global\W2G-L2PortBlock`
(`scripts/l2/L2PortLock.psm1`) before its build and holds it through teardown; a queued run prints
`L2_PORT_LOCK_WAITING` then `L2_PORT_LOCK_ACQUIRED` and may wait up to an hour. **Lock order is port
lock first, desktop lock second, released in reverse — never take the port lock while holding the
desktop lock**; that order is what keeps the two from deadlocking. A checkout older than the lock
takes none, and that includes `run-journey-g3.ps1` until its ControlServer binding reaches a commit
that has it. What the lock cannot stop, the startup waits catch: `Wait-L2Condition -Port` fails
unless the port is held by the component this run started, and names whoever holds it instead.
Anything new that binds this block must take the lock the same way, inside the script. The self-check
is `scripts/l2/Test-L2PortLockQueueing.ps1` (about a minute; it runs two real orchestrators).

A scenario whose sibling `scenarios/<name>.setup.psd1` says `Onboard = 'Real'` runs a second rig
instead: the shipped onboard WPF from `8005-agv-onboard-hmi` driven through UI Automation, plus
the real `slots-simulator` over Modbus. `real-onboard-normal-load` is its baseline, about 23
seconds. Three things to know before running one:

- **Two WPF windows appear on the desktop.** The rig needs an interactive session, so it cannot
  run over SSH or in a service-mode runner, and it steals focus once at startup.
- **It takes a machine-wide desktop lock, and may queue for up to 30 minutes.** `win11-01` has one
  interactive desktop and hosts runners for four repositories, so `8005-mes-ingest`'s golden
  renderer and desktop test suite compete for it. The lock is the named mutex
  `Global\W2G-InteractiveDesktop`; `scripts/DesktopLock.psm1` is this repository's copy and
  `8005-mes-ingest/Invoke-WithDesktopLock.ps1` is the canonical definition. **The name is the
  contract, the code is not** — the two repositories are independent clones with no shared package,
  and `Test-DesktopLockQueueing.ps1` on each side asserts the literal so it cannot drift into two
  locks that never meet. A queued run prints `DESKTOP_LOCK_WAITING` and then
  `DESKTOP_LOCK_ACQUIRED`; silence is a hang, not a queue. The rig takes it after the port lock
  above, so a run queued here keeps synthetic runs queued behind it. The other three holders here are
  `run-staged-g3.ps1`, `run-staged-g3-restart.ps1` and
  `Invoke-AuthorizedAbsentObservationShadow.ps1` — **anything new that starts a WPF peer must take
  it too**, acquired inside the script rather than in a wrapper, because these scripts are run by
  hand at least as often as from CI and a wrapper leaves the bare invocation unprotected.
- **Neither peer repository is built in place.** Each is cloned to
  `%LOCALAPPDATA%\8005-l2-peers\` and published from the clone; configuration is patched only in
  the per-run stage copy. A dirty peer worktree aborts the run rather than testing the committed
  state behind your back — **and since 2026-09-04 that check has teeth**, because the development
  work on both peers is ours now and their worktrees do get edited. Commit to your `w2g/*` branch
  before running L2.
- **A PASS still proves nothing about real hardware.** The simulator proves the software IO loop,
  not modules, wiring, locks or light curtains.

Gates cost far more than the suite. **Do not enter one on your own initiative** —
say which gate, what it costs, and what it proves, then ask:

| Gate | Command | When |
| --- | --- | --- |
| `G1` | `pnpm g1` in the protocol repo | Protocol content manifest and attestation check |
| `CONTROL_SERVER_G2` | `.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice <id> -ProtocolManifest <protocol-repo>\manifest\release.json -Output <new dir>` | This side's per-slice conformance |
| `G3` | `.\scripts\run-staged-g3.ps1`, `run-staged-g3-restart.ps1`, `run-demand-bearing-g3-vectors.ps1`, each with `-StageRoot <short path that does not exist> -EvidenceRoot <new dir>`; the third one also needs `-FieldRunRoot <an authorised field run's root>` | Both-ends integration |
| `RC` | `.\scripts\New-WireToGateReleaseCandidate.ps1` | Cutting a release candidate |

Load-bearing details:

- **The G2 entry point verifies the exact protocol manifest hash first.** When
  the protocol version changes, old evidence cannot carry over — `W2G-IS-01` was
  remapped to `CV-DEMAND-ACCEPT-TO-PICKUP` in `protocol-v0.1.1`, which already
  voided the `v0.1.0` G2 evidence once. The v2 identity switch is the second
  time: every `protocol-v0.1.1` gate result is bound to a manifest hash the
  server no longer sends.
- **`-Output` and `-EvidenceRoot` must be new directories.** Never overwrite
  existing evidence.
- `run-staged-g3.ps1` needs Node.js and pnpm because it runs the protocol's G1.
  All three G3 runners are plaintext, run unattended, and share the four commit
  bindings in `run-staged-g3.ps1`'s param block.
- **`run-demand-bearing-g3-vectors.ps1` takes a third mandatory parameter**,
  `-FieldRunRoot`: the root of an authorised field run, whose `controlserver.db`
  it restores read-only as the demand-bearing store. `C:\Users\szy\w2g-stage\run\fullloop-20260829T131549Z`
  is the one used so far. Without it the runner exits before doing anything.
- **Check the commit bindings before a G3 run, and move them.** They are literal
  defaults, so a run inherits whatever the last run froze and silently gates old
  code — on 2026-09-04 they still pointed at a ControlServer and an onboard from
  several days earlier. `$OnboardCommit` must be the tip of `origin/OnboardHmi_MVP`,
  which `New-ExactClone -RemoteRef` enforces; the other three are unchecked.
- **Evidence for individual G3 vectors is not the same as eight slices passing.**
  A slice passes only with all four gates.
- Full procedure: `docs/RELEASE-CANDIDATE.md`.

## Evidence discipline

- Evidence goes under `evidence/g2/`, `evidence/g3/`, `evidence/rc/`, one new
  directory per run.
- **Keep red evidence.** Never hide a failed run behind a successful re-run.
  Preserve the first failure, explain the cause, and add a regression test where
  possible.
- Defects go in `docs/defects/`, opening with a `Found by:` line that links the
  run that found them.
- Historical red evidence and released identities are immutable.

## Toolchain baseline

This repository is pinned to the workspace-wide .NET toolchain. The authority is
`8005-agv-program/docs/adr/cross/0056-dotnet-toolchain-baseline.md`.

| Item | Pinned value | Enforced by |
| --- | --- | --- |
| SDK | 8.0.424, `rollForward: disable` | `global.json` |
| Target framework | `net8.0` | `Directory.Build.props` |
| Test stack | xunit.v3 3.2.2, Microsoft.NET.Test.Sdk 18.8.1, xunit.runner.visualstudio 3.1.5 | `Directory.Packages.props` |
| Banned packages | xunit v2, NUnit, MSTest, coverlet.collector | `Directory.Build.targets` |

Package versions live in `Directory.Packages.props` and nowhere else. xunit v2,
NUnit, MSTest and coverlet.collector are banned — do not add them back, and do
not "upgrade" a test project by switching frameworks. `Directory.Build.targets`
enforces that at build time: a banned package produces `error W2G0056` and fails
the build. It matches item identity exactly, so `xunit.v3` is not caught by the
`xunit` entry. Verified on 2026-09-04 by adding one deliberately.

**An inline `Version=` is a different story, and this file used to get it
wrong.** It does not fail restore with NU1008. Under central package management
NuGet silently ignores it — the central version wins, no error, no warning, and
`%(PackageReference.Version)` is empty in every MSBuild target, so no build-time
guard can see it either. `CentralPackageVersionOverrideEnabled=false` governs the
`VersionOverride` attribute, not `Version`. The version therefore never actually
drifts, but whoever wrote the inline one is not told it was ignored. Only
`check-toolchain.ps1` catches it, by reading the csproj as text. Do not write an
MSBuild target for it — one was written and deleted after it passed every case it
existed to fail.

Never raise a version in one repository alone. Change the ADR and every
repository together, then run `check-toolchain.ps1` from the workspace root; it
reports drift across all seven repositories and exits non-zero when a writable
one deviates.

## Scripting baseline

PowerShell 7. Do not write Windows PowerShell 5.1 compatible code, do not add
version probes or fallbacks, and do not invoke `powershell.exe` — call `pwsh`.
Every new `.ps1` opens with `#Requires -Version 7`.
