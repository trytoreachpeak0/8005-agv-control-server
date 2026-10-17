#Requires -Version 7
# W1 slot probe for agv01 against database C (2026-10-15, new slot model id after re-seed). Authorised by the user in chat.
& F:/8005-wt/rig-req0263-8005-agv-control-server/scripts/field/Invoke-W1SlotIoProbe.ps1 `
    -SiteAlias agv01 -Order 1 -AgvId '老厂前线新多仓位1' -IoModuleHost 192.168.71.150 `
    -SlotModelVersionId 74da3fee0e164c3e89b161c306ba5edb -VerifiedBy 'Zhengyu Shao' `
    -RecordDirectory C:/Users/szy/Desktop/8005-workspace-v2/evidence/field/20261014-req0263-slot-verify/agv01-dbC
"probe exit=$LASTEXITCODE ok=$?"
