# Half a checkout on a reused runner workspace

l2 run 39457961360 (PR #817), both attempts on vm-win-01's headless runner `vmwin01-control-server`:
actions/checkout@v7 printed `Path '...' not uptodate; will not remove from working tree.` for every file the new tree
drops, exited 0, moved HEAD to the right merge commit, and left `ControlServer.sln` out; the build step failed MSB1009.

## Cause

- The message is git's sparse-checkout warning (`WARNING_SPARSE_NOT_UPTODATE_FILE` in unpack-trees), not a dirty or
  locked file: it is printed when a checkout applies sparse patterns. Line endings and file locks were ruled out by
  reproducing it on a throwaway repository with `core.autocrlf=false` and nothing else running
  (`red/01-selfcheck-before-fix.txt`, the three `history:` cases).
- `diagnosis/01-vm-win-01-runner-workspaces.txt`: three of the six workspaces have `core.sparseCheckout=true` in
  `.git/config`. That setting is written there only by actions/checkout's `sparse-checkout-cone-mode: false`
  (`git config core.sparseCheckout true`), which control-server#804 brought into l2.yml and release.yml on 2026-10-20.
- A later job that asks for no sparse checkout runs `git sparse-checkout disable` and then
  `git config --local --unset-all extensions.worktreeConfig`. disable writes `core.sparseCheckout=false` into
  `.git/config.worktree`; unsetting the extension hides that file, so the `true` in `.git/config` is in force again with
  whatever patterns are left. On the headless runner those were a g3 gate job's cone list `scripts`, read as non-cone
  patterns: only paths named `scripts` survive. `diagnosis/02-job-118799921289-checkout.txt` is the job's own log.
- Before control-server#820 the scenarios job ran on the headless runner, which also runs main's and older branches'
  workflows and g3.yml's gate and report jobs; so one runner saw all three kinds of checkout.

## Fix and evidence

- `red/01-selfcheck-before-fix.txt`: `scripts/Test-CheckoutSparseState.ps1` against `origin/fp/v2-impl` before the fix,
  43 cases red, among them the replay of the runner history (ControlServer.sln missing, the same warning, exit 0).
- `green/01-selfcheck-after-fix.txt`: the same self-check with the fix, 110 cases.
