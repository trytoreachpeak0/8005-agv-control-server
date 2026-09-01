# Ticket 11 — publication of `w2g-mvp-rc-0.2.0`

Published 2026-09-01 from the plaintext-transport Wayfinder map. Every verdict
below is a read-back of what GitHub actually serves, never an upload return
code, and every detector was exercised in both directions.

| | |
| --- | --- |
| Release | <https://github.com/trytoreachpeak0/8005-agv-control-server/releases/tag/w2g-mvp-rc-0.2.0> |
| Tag | `w2g-mvp-rc-0.2.0` (annotated) → `19ce7db70893afea6c6361988c3bc612d77569d0` |
| Candidate | `w2g-rc-20260901b-19ce7db` (ControlServer `19ce7db` + OnboardHmi `238b46e` + `protocol-v0.1.1`) |
| Verdicts | **16 / 16 as expected**, RED half of every detector fired |

Reproduce with `Verify-Publication.py` (archived here) after
`gh release download w2g-mvp-rc-0.2.0 --dir <downloads> --clobber`. Raw output
is `verify-publication.txt`.

## 1. Remote assets, re-hashed after download

All three came back byte-identical to what was recorded before upload, at the
sizes the release notes state:

| Asset | Size | SHA-256 |
| --- | --- | --- |
| `w2g-rc-20260901b-19ce7db.zip` | 123,991,314 B | `abbff6e0d9afe415080b774395695bff4dad9f6437a9455f3e3c6d84aa1ceca9` |
| `release-manifest.json` | 171,966 B | `2389853936f9453c2fa93168c6d1316f0e83ba0367246801be98653aa54df3d9` |
| `SHA256SUMS.txt` | 99,003 B | `5e3aeb3ab94a29723b9c7e2c79b5bbc2548777a78f181295368363e7e7cdbd02` |

RED for each: the same comparator, fed a one-character mutation of the expected
digest, returns MISMATCH. Three for three.

## 2. The downloaded zip is internally consistent

Beyond what the ticket asked for. The zip GitHub serves was opened and checked
against its own manifest without ever unpacking it to disk:

- 870 entries under a single top-level directory `w2g-rc-20260901b-19ce7db/`;
- **869 / 869 hashes match**, 0 mismatch, 0 missing;
- the two standalone assets are byte-identical to the same-named files inside
  the zip — which is what section 2 of the release notes claims, so it is read
  back rather than asserted.

## 3. The tag peels to the commit the candidate records

The tag was re-fetched from the remote into a scratch ref rather than trusted
locally. It is an annotated tag object, and it peels to
`19ce7db70893afea6c6361988c3bc612d77569d0` — identical to
`components.controlServer.commit` in the downloaded manifest, and to the 40-hex
SHA the ticket froze. The full SHA was used at tag creation on purpose:
`HEAD` or a branch name would mis-target if the owner pushed while we worked.

RED: the same comparison against a one-character mutation of the manifest
commit returns MISMATCH.

**`19ce7db` is not the branch head.** Evidence commits and the
`f0d6f1b` fix (see §5) all land after the candidate; the tag deliberately
points behind them.

## 4. Both live bodies are verbatim their local copies

Normalisation is line endings only — the text itself is compared character for
character. `gh --jq` output is landed on disk before comparison, since
PowerShell splits it into an array and a naive comparison would read false-red.

| Body | Local | Live | Verdict |
| --- | --- | --- | --- |
| `w2g-mvp-rc-0.2.0` | 13,221 chars | 13,221 chars | **VERBATIM MATCH** |
| `w2g-mvp-rc-0.1.1` | 10,482 chars | 10,482 chars | **VERBATIM MATCH** |

RED: one character flipped in the local copy breaks the same comparator.

The 0.1.1 edit added a back-reference block at the top of the body — placed at
the top, not appended, because "not interoperable" is an operational warning
and would be invisible 204 lines down. **The 0.1.1 tag and assets were not
touched**: its three asset digests are unchanged from before the edit
(`12a7ce56…`, `e607a9d9…`, `727b3fbb…`).

## 5. Recorded as-is

- **`f0d6f1b` is deliberately outside this release.** The fix for the
  post-completion demand replay conflict ("Stop offering an already-accepted
  demand back to journey intake") was committed after the candidate and is not
  in these binaries. It changes `JourneyRuntimeEngine`'s candidate scan — the
  very path the ticket 10 field closed loop exercised — so folding it in would
  have required rebuilding the candidate, tier 1, a fresh clean-install
  acceptance and **another real-vehicle closed loop**. The user chose to ship
  0.2.0 on the evidence that actually covers it and carry the fix into a later
  version. Known limitation 7 in the release notes names the fix and its
  commit, and states plainly that this version does not have it. The commit was
  pushed to `ControlServer_MVP` but is not referenced by any tag.
- **No tag or release was created in `8005-agv-onboard-hmi`.** That repository
  is read-only to agents and its version references belong to its owner;
  section 8 of the notes says so. `protocol-v0.1.1` is untouched.
- **No file inside the package was modified before upload.** The zip was built
  from the candidate root as-is; the 869 hashes verified before upload and again
  after download are the same 869.
- The three plaintext known limitations are stated without softening, as the
  ticket required, and the premise ("a controlled factory intranet") is stated
  as a precondition rather than a mitigation.
