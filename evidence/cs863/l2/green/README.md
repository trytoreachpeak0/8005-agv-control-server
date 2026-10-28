Green run of `charging-order-not-dispatched-holds-charger` on 3fa5c3e55 (this branch, after independent review S1/S2/L1/L2).
The pre-review green run on 95d6f0f1f is in this file's history (commit bcf842ae2).

    pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#863 pwsh -File <worktree>/scripts/l2/Invoke-L2Scenario.ps1 -Scenario charging-order-not-dispatched-holds-charger -EvidenceRoot C:/work/8005-scratch/cs863-l2/green-3 -BatchId program-210

PASS on L2-COND-00, L2-COND-01, L2-COND-02.
