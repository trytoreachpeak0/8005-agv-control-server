# 缺陷：恢复原操作的命令带恢复动作哈希，真车载端一律拒绝执行

Status: fixed（服务端）；车载端不改
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs`、`src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs`）
Found by: 2026-09-14 G3 `FP-IS-07` 真车载端场景 `g3-exception-resume` 的调试运行 `resume-004`（红证据在 `C:\g3dbg\resume-004`，未入库）
Product at discovery: `fp/b2-close@8d430f76`；车载端 `w2g/b3-on-v2@8d19fee`（产品代码同 `f14f8af`）
Fixed in: 见提交记录（本单与修复同一提交）

**红在两端之间。两道 G2 都绿：各自的测试都按自己对 `commandContentSha256` 的理解构造恢复命令。**

## 现象

`resume-004`：装载以 `UNKNOWN` 结束 → 车载端重启对账 → 维护人员在车上点「申请恢复」→ 服务端打开恢复会话、接受
`RESUME_AFTER_REPAIR`、下发 `SlotOperationResumeCommand`（检查点 `SAFE_FINISH_REACHED`、仓位 `[1]`）。车载端日志：

```
Warning  WireToGateBusinessService  恢复命令未执行：attempt=7461d1d9-…，reason=RECOVERY_STATE_MISMATCH。 | InvalidDataException: RECOVERY_STATE_MISMATCH
```

命令一直没被确认，恢复工作流停在 `AwaitingResult`，仓门没有再开。

## 原因

协议 `SlotOperationResumeCommand.commandContentSha256` 只规定是 SHA-256，没写算法，两端各按一种理解实现：

- 服务端 `QueueActionCommandAsync` 对恢复原操作与故障交接、强制机械恢复共用 `RecoveryCommandHash.ForRecoveryAction(workflowId, demandId, attemptId, slotsJson, generation)`，
  并在 `RequireResumeAuthorizationAsync` 里用同一算法重算，核对替换结果的范围。
- 车载端 `WireToGateSlotOperationExecutor.ResumeAsync` 要求它等于日志里原 `SlotOperationCommand` 的 `CommandContentSha256`：
  恢复只能照原命令重做，不能凭一条新命令造出新操作。车载端单元测试也按原命令哈希构造恢复命令。

两个值按定义不会相等，所以真服务端上的「恢复原操作」从来没有执行过。

## 决定与修复

2026-09-14 用户裁定：服务端改为下发原命令哈希，车载端不改。

- `OnboardRecoveryCoordinator`：`RESUME_AFTER_REPAIR` 的恢复命令携带并绑定 `StationOperationRow.ContentHash`（即下发给车载端的原 `SlotOperationCommand.commandContentSha256`）。
  故障交接与强制机械恢复仍用恢复动作哈希，不变。
- `WireToGateStore.RequireResumeAuthorizationAsync`：替换结果不再重算恢复动作哈希，改为逐项核对——需求号等于授权、仓位集合正好等于授权、
  工作流绑定的命令哈希等于这笔操作的原命令哈希；强制恢复代数核对不变。超出范围仍抛 `BusinessIdentityConflictException`。

选服务端而非车载端的理由：与车载端写明的「只照原命令重做」一致；恢复命令不再依赖两端强制恢复代数的收敛时机（票 21 撤回过同类比对）；
车载端无需再发版。

## 证据

| 项 | 结果 |
| --- | --- |
| `RecoveryStateMachineG2Tests.ResumeAuthorizationPersistsFormalCommandBeforeSendAndReplaysSameIdentityAfterRestart` 新增断言：恢复命令的 `commandContentSha256` 等于装载操作的原命令哈希 | 修复前红（`Expected: "aaaa…"`，`Actual: "5cc25d5c…"`），修复后绿 |
| 既有 `ResumeAdmitsExactlyOneReplacementResultForTheOperationThatAlreadyFailedItsFirstResult`、`ResumeRejectsAReplacementResultReportedOutsideTheAuthorizedSlotScope` | 修复后仍绿：只放行一份替换结果；只报 1 号仓的替换结果仍被拒 |
| `RecoveryStateMachineG2Tests` | 15 passed |

真车载端上的完整顺序由 G3 `FP-IS-07`（`g3-exception-resume`，断言 G3-07-06..11）核对，证据出来后补进本表。
