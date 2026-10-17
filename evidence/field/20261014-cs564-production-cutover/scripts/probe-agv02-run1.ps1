#Requires -Version 7
# W1 slot probe for agv02, production cutover 2026-10-14, run1. Authorised by the user in chat (unlock, Zhengyu Shao on site).
& F:/8005-wt/rig-req0263-8005-agv-control-server/scripts/field/Invoke-W1SlotIoProbe.ps1 `
    -SiteAlias agv02 -Order 2 -AgvId '老厂前线新多仓位2' -IoModuleHost 192.168.71.150 `
    -SlotModelVersionId 51caf7a3a1a241a1beb8d8269a1744e0 -VerifiedBy 'Zhengyu Shao' `
    -RecordDirectory C:/Users/szy/Desktop/8005-workspace-v2/evidence/field/20261014-req0263-slot-verify/run1
"probe exit=$LASTEXITCODE ok=$?"
