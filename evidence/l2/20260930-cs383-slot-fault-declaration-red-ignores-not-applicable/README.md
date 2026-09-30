# 红证据：结果处理把 NOT_APPLICABLE 也当成生效（control-server#383）

在实现提交 `045935a5` 的工作区上临时注入，跑完即用备份还原（`git diff` 为空后才提交本目录）。注入位置
`src/ControlServer.Host/Transport/SlotFaultDeclarationResults.cs`，`declaration.ResultReceivedAt = receivedAt;` 之后：

```csharp
// INJECTED DEFECT (red evidence, control-server#383): a refusal blocks the operation as if it had applied.
if (state == SlotFaultDeclarationStates.NotApplicable)
{
    foreach (StationOperationRow op in dbContext.StationOperations.Where(row => row.SlotOperationAttemptId == attemptId))
        op.Status = ControlServer.Domain.StationOperationStatus.RecoveryRequired;
    foreach (AcceptedDemandRow demand in dbContext.AcceptedDemands.Where(row => row.DemandId == declaration.DemandId))
        demand.Status = ControlServer.Domain.DemandExecutionStatus.RecoveryRequired;
}
```

跑之前写下的预期：只有 `L2-SFD-03`（被拒的判定不改业务状态）变红，之后因为车被阻断在装货站，场景在「等装货完成」处超时中止。
实际一致：`L2-SFD-01`、`L2-SFD-02` PASS，`L2-SFD-03` FAIL，读到 `Blocked / RecoveryRequired / RecoveryRequired`；随后
`Timed out after 120s waiting for: the load completed and the journey reached the gate leg. Last observed: "Blocked"`。

只保留摘要、判据、时间线与四张表的快照；日志与其余快照未入库。
