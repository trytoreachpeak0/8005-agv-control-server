Red run of `charging-order-not-dispatched-holds-charger` on the defect.

Rig: detached worktree at local commit 3860c9d02 = `fp/v2-impl` 3e62d65b9 product code and tests,
plus this branch's scenario, setup and orchestrator (`scripts/l2/`, `.github/workflows/l2.yml`).
The first attempt (85c1e17e9, 10-25) still carried the new test files and failed to build, so it is not used.

    pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#863 pwsh -File <rig>/scripts/l2/Invoke-L2Scenario.ps1 -Scenario charging-order-not-dispatched-holds-charger -EvidenceRoot C:/work/8005-scratch/cs863-l2/red-2 -BatchId program-210

FAIL on L2-COND-01: no CHARGING_ORDER_NOT_DISPATCHED, 0 alarms. L2-COND-00 and L2-COND-02 pass on the defect:
the cancel-then-clear path already existed; COND-02 guards the field procedure.
