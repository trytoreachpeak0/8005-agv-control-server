# Release scan gate and commit-binding port (map ticket 24)

Ticket 21 reviewed `ea8dc98..ee54988` and recorded five script- and evidence-layer deviations
alongside the two product defects that ticket 22 closed. This directory holds the evidence for the
two of them that were fixed. **No product code was touched and tier 1 was not run**; nothing here
builds, installs or runs a release candidate.

## What was fixed

| Deviation | Change |
| --- | --- |
| 4 — the release script recorded its scan findings and packaged regardless | `Assert-ReleaseScanGate` in `scripts/New-WireToGateReleaseCandidate.ps1`, called after `inventory/` is written and before the manifest |
| 4 — `Get-PackageLicense` hard-coded `%USERPROFILE%\.nuget\packages` | `Get-NuGetGlobalPackagesRoot` honours `NUGET_PACKAGES` first |
| 1 — `run-staged-g3-restart.ps1` still accepted a `-CommitBindingSource` override | parameter removed; the path is a fixed `$PSScriptRoot`-relative constant |

The gate fails the release on any secret-scan finding, any key-material file, and any unresolved
license outside a named allowlist. The allowlist is exactly the three first-party RIoT SDK packages
that ticket 11 finding #3 already reported, listed by id — so a **new** dependency without license
metadata fails the release instead of joining a count. The gate runs after the inventory files are
on disk, so a failed release still leaves the evidence needed to diagnose it, and before the
manifest, so it can never produce a package that claims to have passed.

Failure messages carry `path:line:rule` only. The scan documents `matchedValuesDisclosed = false`,
and a message that quoted the match would print the secret into every console and CI log.

## Falsifiability

Green assertions are only evidence once they have been shown to turn red. Both harnesses lift the
shipped rules, scan and gate out of the committed script **by AST**, so they exercise the code that
ships rather than a copy, and compare it against the same code at the `0e4d471` baseline.

| Harness | Result |
| --- | --- |
| `Test-ReleaseScanGate.ps1` → `scan-gate-result.json` | PASS, 12/12 |
| `Test-CommitBindingPort.ps1` → `commit-binding-port-result.json` | PASS, 5/5 |

The red side is asserted directly, not assumed:

- a planted `"apiKey"` literal is detected and **throws** (4, 11), while the baseline is shown to
  contain no gate at all (8);
- a `.pfx` throws (6); an unlisted unresolved license throws while the three allowlisted ones do
  not (7);
- the failure message is asserted **not** to contain the planted value (5);
- `Get-PackageLicense` resolves through `NUGET_PACKAGES` now (9) and returned `UNRESOLVED` for the
  identical package at the baseline (10);
- the restart runner's override parameter existed at the baseline (1) and no longer binds (2, 3),
  while the four commits it reads are unchanged (4) — and a stale copy is shown to be accepted by
  the reader without error (5), which is why the path must not be caller-supplied at all.

The gate is **not retroactive** to the package that already exists, so the last assertion runs it
over that run's own archived inventory instead: the `2eeb6f0` release candidate passes (12) — 0
findings, 0 key-material files, and its 3 unresolved licenses are exactly the allowlisted RIoT SDK
packages. It would not have been blocked.

## Not fixed here

Deviations 2, 3 and 5 are addressed in the ticket's answer. Deviation 3 (the missing release
artifacts) was archived into `../20260830-isolated-install-2eeb6f0/release-artifacts/` after
verifying both root hashes still match that run's own record.

Those artifacts carry a `-text` rule in `.gitattributes`. Under the repository's `text=auto eol=lf`
default the first commit stored `release-manifest.json` at 162,774 bytes instead of 171,544, so a
checkout would have hashed to something other than the value the evidence claims. Both files are now
verified to hash to `97468cae…` and `221ea67c…` when read back out of the object store.
