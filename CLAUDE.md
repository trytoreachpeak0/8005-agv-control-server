# 8005-agv-control-server

The production server repository for the WIRE_TO_GATE MVP. This file is the
agent's instructions for working here.

## Write authority

This repository is writable. The others in the workspace are not, each in its
own way:

- `8005-agv-onboard-hmi` and `slots-simulator` — **writable on `w2g/*` branches
  only** (changed 2026-09-04; they were read-only for agents before). The
  development work on both is now ours, and a change reaches their working
  branches (`OnboardHmi_MVP` and `main`) only as a pull request — **since
  2026-09-09 both the agreement and the merge are ours**, with nobody else to
  wait on. Direct pushes, force-pushes, tags and releases on those branches stay
  forbidden.
- `8005-agv-protocol` — writable, with no announcement duty since 2026-09-08.

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

### Deciding what to work on

The workspace ships a `w2g-next` skill. When the next step is unclear, it reads
the real state — working tree, the slice board issue, `integration-slices/index.json`,
open issues, gate evidence — and applies a fixed priority ladder to name one
action. Prefer it over guessing.

## Collaboration workflow

Two people drive this project. Kun Wang (GitHub `SocialKKKK`) owns
`8005-agv-onboard-hmi` and `slots-simulator`, but **the development work on both
has been ours since 2026-09-04** — we work them on `w2g/*` branches and deliver
by pull request, which since 2026-09-09 we agree to and merge ourselves. Zhengyu
Shao owns this repository;
`8005-agv-protocol` is jointly maintained. The full account, written for humans
and in Chinese, is `8005-agv-program/docs/collaboration-workflow.md`.

What an agent must follow:

- **The unit of collaboration is the integration slice.** Do not invent another
  one. `8005-agv-protocol/integration-slices/index.json` defines `W2G-IS-00`
  through `W2G-IS-07`, each with a `sequence` and `prerequisites`. Each slice's
  `gates` array *is* the division of labour: `G1` shared, `CONTROL_SERVER_G2`
  this repository, `ONBOARD_HMI_G2` **ours too since 2026-09-04** (it was Kun
  Wang's; taking over the onboard code did not move it automatically, and the
  user then moved it deliberately), `G3` ours as well — **one person has run it
  since 2026-09-08**; all three runners are loopback and unattended, so "both
  people present" was governance, not a technical need, and nobody was left to
  satisfy it.
- **The two G2 gates have no dependency and run in parallel.** That is still
  true, but since both are ours it now means two of our own workstreams, not two
  people. A red `ONBOARD_HMI_G2` is no longer someone else's blocker — it is work
  to pick up on a `w2g/*` branch.
- **Moving that gate did not move the release signature — but 2026-09-08 did.**
  This bullet used to say the attestation needed two distinct product owners with
  `G1` checking `size===2` for real, Kun Wang being the second. The user then took
  over his project outright, which left a rule demanding a signature nobody could
  give; the rule was changed rather than bypassed (`docs/release-governance.md`,
  the attestation schema and `g1-validate.mjs` together, `size===2` → `size===1`),
  and `protocol-v0.2.0` shipped under it. **One owner now — and "one owner" means
  the user signs, never an agent. AI and CI still cannot approve.**
- **Cross-repository feedback takes one of three routes.** A contract ambiguity
  or error goes to an issue in `8005-agv-protocol` carrying the `vectorId` that
  triggered it. A peer repository failing the contract goes to an issue in *that*
  repository — **run G3 for evidence first** and attach the evidence directory —
  but since 2026-09-04 such an issue is a ticket **we** pick up on a `w2g/*`
  branch, not a request left with someone else. Work inside this repository stays
  in this repository's issues.
  **A cross-repository claim must carry reproducible gate evidence; "it does not
  work on my side" is not a report.** Follow the shape already used in
  `docs/defects/`: a `Found by:` line linking the G3 evidence `SUMMARY.md`.
- **`8005-agv-protocol` needs no advance approval and no announcement** — Zhengyu
  Shao decides its content alone. The announcement duty (an issue that
  `@SocialKKKK` after every push) was dropped on 2026-09-08 when the user took
  over his project: nobody else implements against this contract now, and there
  is no evidence of his left to void. **Do not `@SocialKKKK` anything.** Still
  state what changed, which `W2G-IS-*` slices it touches and which gate evidence
  is now void — in the commit message, because that evidence is ours. Tagging a
  release needs a one-owner attestation (two before 2026-09-08); **AI and CI
  cannot approve**, and "one owner" means the user signs, never an agent.
- **Batch protocol changes.** A patch release voids the affected G1/G2/G3
  evidence — all of it ours — so every small change costs us a full gate re-run.

## Language

Agent instruction files — this one, and anything under `.claude/` — are written
in **English**.

Everything a human reads is written in **Chinese**: README files, documentation
prose, `docs/defects/` entries, evidence summaries, issue and pull-request
titles and bodies, and commit message bodies.

Stay English inside Chinese text: conventional commit prefixes (`feat:`, `fix:`,
`docs:`, `chore:`), identifiers, paths, commands, environment variables, error
codes, gate and slice names (`G1`, `W2G-IS-00`), and protocol message names,
schema fields and `vectorId` values — those are the contract itself. Quote an
error or a test result in its original English first, then explain it in
Chinese. Do not rewrite existing text to match; this governs new writing.

## Tests and gates

**"Run the full test suite" means this one command here**, never a gate:

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```

Historically 243–249 passed / 0 skipped. Build with the .NET SDK pinned in
`global.json` (`8.0.425`); when it is not on `PATH`, point
`WIRE_TO_GATE_DOTNET_EXE` at that version's `dotnet.exe` and run
`.\scripts\build.ps1`.

**Run every `dotnet` command from the repository root.** `dotnet` resolves
`global.json` by walking up from the current working directory, not from the
project or solution path it is handed — an explicit `WIRE_TO_GATE_DOTNET_EXE`
included. Anywhere else it silently falls through to the newest SDK installed:
from `C:\Users\szy\Desktop\8005-workspace` the control host builds with
`10.0.302`, which either fails this repository's analyzers or, worse, succeeds
with an unpinned toolchain. `scripts/build.ps1` and `scripts/test-wire-to-gate.ps1`
change into the repository root themselves; a `dotnet` you type does not.
`dotnet --version` where you stand is the check.

**That command also checks every outbound protocol line against the protocol JSON Schema**, and it
can fail while its console summary says `Failed: 0`. When the run ends,
`tests/ControlServer.Tests/OutboundSchemaConformance.cs` hands what the tests sent to
`tools/ControlServer.SchemaConformance` (its own process: its System.Text.Json 10 must not enter the
test host). A violation is reported as `[Test Assembly Cleanup Failure] Xunit.Sdk.TestPipelineException`
with exit code 1; the detail is in the TRX and in `schema-conformance/` next to the test assembly.
Never "fix" such a failure by adding to `tests/ControlServer.Tests/schema-known-violations.json`
without an issue that owns the violation -- that list is for defects already filed, and every entry
names one. It adds about 35 seconds of Roslyn schema compilation per run.

Test authorization is scoped to the current task. A request to inspect, tidy,
commit, or push an already-dirty worktree does **not** authorize a test run. Run
tests only when the current task changed product code, tests, or build inputs,
or when the user asks for validation. Never infer it from `git status`.

Between the suite and the gates sits the **L2 scenario runner**, which is cheap (about 14
seconds of scenario plus 15–20 seconds of schema compilation), unattended and safe to run on your own
initiative:

```powershell
pwsh .\scripts\l2\Invoke-L2Scenario.ps1 -Scenario normal-load -EvidenceRoot .\evidence\l2\<new dir>
```

It starts a real ControlServer against loopback doubles — `tools/ControlServer.FakeRiot`,
`tools/ControlServer.FakeMesIngest` and the synthetic peer in `tools/ControlServer.FakeOnboard` —
drives one scenario, asserts against the server's own database and writes evidence. Use it when a
change touches the journey runtime's cross-end timing, which the unit suite covers only from
inside one process. `-EvidenceRoot` must be a new directory. See `scripts/l2/README.md`; a PASS
there proves nothing about real hardware.

Every run also judges `L2-SC-01`: each line the onboard side sent -- the synthetic peer's own record,
or the real onboard's rows in `ProtocolInbox` -- against the protocol JSON Schema, with the same
validator and known-violation list as the unit suite. That is the only place either is ever checked,
so a red `L2-SC-01` is a peer sending a line the contract forbids, not a flaky scenario.

A scenario whose sibling `scenarios/<name>.setup.psd1` says `Onboard = 'Real'` runs a second rig
instead: the shipped onboard WPF from `8005-agv-onboard-hmi` driven through UI Automation, plus
the real `slots-simulator` over Modbus. `real-onboard-normal-load` is its baseline, about 23
seconds. Three things to know before running one:

- **Two WPF windows appear on the desktop.** The rig needs an interactive session, so it cannot
  run over SSH or in a service-mode runner, and it steals focus once at startup.
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
| `CONTROL_SERVER_G2` | `.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice <id> -ProtocolManifest .\vendor\8005-agv-protocol\protocol-v0.3.0\manifest\release.json -Output <new dir>` — the vendored copy, byte-identical to the tag; the protocol working tree's manifest moves with every commit after the tag and fails the hash check | This side's per-slice conformance |
| `G3` | `.\scripts\run-staged-g3.ps1`, `run-staged-g3-restart.ps1`, `run-demand-bearing-g3-vectors.ps1`, each with `-StageRoot <short path that does not exist> -EvidenceRoot <new dir>`; the third one also needs `-FieldRunRoot <an authorised field run's root>` | Both-ends integration |
| `RC` | `.\scripts\New-WireToGateReleaseCandidate.ps1` | Cutting a release candidate |

Load-bearing details:

- **The G2 entry point verifies the exact protocol manifest hash first.** When
  the protocol version changes, old evidence cannot carry over — `W2G-IS-01` was
  remapped to `CV-DEMAND-ACCEPT-TO-PICKUP` in `protocol-v0.1.1`, which already
  voided the `v0.1.0` G2 evidence once.
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
| SDK | 8.0.425, `rollForward: disable` | `global.json` |
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
