# journey G3 两个场景第一次实跑就红：脚本没跟上 v3 的界面与告警语义

Found by: [`evidence/g3/20261010-protocol-v3.0.0-journey-5f3adc42/`](../../evidence/g3/20261010-protocol-v3.0.0-journey-5f3adc42/)（control-server#393 出口第一轮 G3，`JOURNEY_G3_SLICE_FAIL`，20 个场景中 18 个 PASS）

这两个场景之前都没真跑过。PR #424 写明 `g3-slot-fault-declaration` 的 journey 要等车载端 hmi#215，当时没跑；PR #425 写明 `g3-forced-mechanical-recovery` 的 v3 改动「要到 cs#393 出口时一起真跑，本次只做了本机解析」。所以这次出口是它们第一次实跑。产品行为在两处都符合设计，红在场景脚本上。

## 一、`g3-slot-fault-declaration`：等卸货超时告警时认到了装货阶段的旧告警

**现象（读到的）**：前四条判据 PASS，即 G3-07-62～G3-07-64 和线上顺序那一条。到卸货站以后，脚本在卸货进入等待操作员的 1 秒后提交了判定，门槛是 20 秒。服务端回：

```
409 {"detail":"SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE", ... "slotNo":1}
```

意思是这一仓还没超时，判定被拒，这个回答是对的。之后脚本在 `g3-slot-fault-declaration.ps1:240` 读 `$applied.Body.declarationId`，StrictMode 下抛出：

```
The property 'declarationId' cannot be found on this object. Verify that the property exists.
```

于是后面的判据都没走到。

**原因（读到的）**：`Wait-Overdue`（`:95-103`）在全部收到过的 `OnboardAlarmSnapshot` 里，取最后一份「含该仓 `SLOT_EXPECTED_ACTION_OVERDUE` 告警」的快照。装货阶段那份告警（16:47:06，1 号仓）也满足这个条件，所以等卸货超时那一步立刻就返回了。时间线里 `unload-overdue-reported` 的值就是那条 `At=16:47:06` 的旧快照。

**改法**：`Wait-Overdue` 只认这一次卸货进入等待之后收到的快照，或者按告警的 `raisedAt` 过滤。

## 二、`g3-forced-mechanical-recovery`：交接框没填就等确认按钮可用

**现象（读到的）**：场景按下「强制机械恢复」，在确认框点了「是」。然后等「已隔离并完成机械取出」按钮，60 秒没等到，五条判据都记为未到达。

服务端日志里没有相关记录，但收尾时的库快照证明服务端已经收到申请、并下发了命令：

- `db-ExceptionRecoverySessions`：会话 16:44:20.53 打开，状态 `EXECUTING`，动作 `FORCED_MECHANICAL_RECOVERY`，`ForcedRecoveryGeneration=1`。
- `db-RecoveryWorkflows`：处于 `AwaitingResult`，`ForcedMechanicalRecoveryCommand` 已签发。

**原因（读到的）**：hmi#216 之后，「已隔离并完成机械取出」按钮会出现，但只有交接框都填好才可用。这个按钮的可用状态绑定 `CanSubmitForcedMechanicalRecoveryConfirmation`（车载端 `MainWindow.xaml:532-538`）。按 `MainViewModel.cs:1442-1458`，有需求时，子批号或接收人缺一项，按钮就不可用。

脚本第 60 行用 `Wait-G3ButtonOffered` 等这个按钮，而这个函数看的是 `IsEnabled`（`L2.psm1:802-807`）。填交接框的代码在第 70-76 行，排在等待之后。按钮要等框填好才可用，框要等按钮可用才去填，所以这 60 秒注定空等。

**改法**：
1. 先等交接框出现，填好两个框。
2. 再等按钮可用，然后按「已隔离并完成机械取出」和「确认强制机械取出」。
3. 确认失败时车载端弹的框标题是「强制机械取出未上报」（`MainWindow.xaml.cs:336-338`），脚本第 84 行要把这个标题也认上。

## 去向

两处与 staged 的四条判据一起由 control-server#541 改脚本，不改 `src/`。合入后本票四个 runner 从头重跑。本轮的红证据原样保留。

## 第二轮：修掉第一层之后露出的下一层（control-server#555）

Found by: [`evidence/g3/20261010-protocol-v3.0.0-journey-3411887d/`](../../evidence/g3/20261010-protocol-v3.0.0-journey-3411887d/)（cs#393 第二轮，绑定 `3fa909e4`，18/20）

cs#541 修好上面两处之后，两个场景都走得更远了，然后各红在下一处同样从未执行过的脚本分支上。产品行为依然正确。

- **`g3-slot-fault-declaration.ps1:293`**：前 6 条判据 PASS，cs#541 的修复也生效了（卸货超时告警这次等到的是新的）。之后 G3-07-67 的实际值读 `$result.PayloadJson`，可是 `$result` 来自 `Get-L2Inbound`，它的行只有 `MessageId`、`At`、`Payload`、`Response`、`ResponsePayload`。StrictMode 下抛 `The property 'PayloadJson' cannot be found on this object.`。来源 `2bb90911`（cs#383）。
- **`g3-forced-mechanical-recovery.ps1:170`**：5 条判据里 4 条 PASS。G3-07-44 用 `$null -eq $closedReason -or $closedReason -is [System.DBNull]` 判断「原因为空」，但 `Get-G3Scalar` 返回 `[string]$rows[0].Value`，NULL 会变成 `""`，所以这一项永远为假。库快照 `db-ExceptionRecoverySessions.json` 里 `ClosedReason` 是 `null`。来源 `fbc89901`（cs#385）。

两处由 control-server#555（PR #557）修，只改 `scripts/`。离线自检 `scripts/l2/Test-G3JourneyV3ScriptBranches.ps1` 修后全绿，对修复前的脚本红在预期的 3 条。

**这一轮顺带看到的事实（不是缺陷，另开票）**：G3-07-44 的实际值里，车辆会话是 `RecoveryRequired (FORCED_RECOVERY_GENERATION_MISMATCH)`，线上码是 `FORCED_RECOVERY_GENERATION_STALE`，而不是「等硬件恢复记录」（`FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED`）。原因是「车载端报过的代数」只在握手时的恢复报告里更新（`WireToGateStore.cs:306`），而结果送达后车载端没有重新握手。这是不是设计预期还没有定，由 control-server#556 承接（出口后做）。G3-07-44 本来就只判 `Readiness`，本次出口照原口径判。

## 第三轮：另两个场景红在界面读写夹具（control-server#560）

cs#555 修好后，第三轮（绑定 `b6be67ac`）这两个场景都 PASS 了，但另两个前两轮都 PASS 的场景 `g3-multi-stop-plan`、`g3-waiting-point-idle-return` 红了，原因不同，单独记在 [`20261010-journey-g3-uia-fixture-reads-not-told-from-product.md`](20261010-journey-g3-uia-fixture-reads-not-told-from-product.md)。第四轮（绑定 `76c9cfe2`）journey 20/20 PASS。
