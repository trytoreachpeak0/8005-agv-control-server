# 缺陷：「申请恢复」被拒之后，车载端「补偿清空」复用被拒动作的 id 与 messageId，服务端判内容冲突断开连接，补偿入口永久不可用

Status: fixed（车载端，v2 线）：`8005-agv-onboard-hmi` `w2g/b3-on-v2@372186f`，移植自 MVP 线 `ab346ed`
Owner repository: `8005-agv-onboard-hmi`（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs`、`WireToGateBusinessService.RecoveryVectors.cs`）
Found by: 2026-09-14 L2 `real-onboard-restart-with-open-recovery-session` 首跑 `ros-001`（为 `8005-agv-control-server#36` 新写的场景）；
证据 `evidence/l2/20260914-real-onboard-restart-with-open-recovery-session-001/`
Product at discovery: 服务端 `fp/b2-close@2b2aa51c`（= `fp/v2-impl`）；车载端 `w2g/b3-on-v2@b960108`（产品代码同 `w2g/fp-v2-impl@8dee1c3f`）
Fixed in: 车载端 `372186f`（`w2g/b3-on-v2`，已推送；十片 `ONBOARD_HMI_G2` 证据 `19a740c`）。服务端未改

**红在车载端产品。服务端把开着的恢复会话重放给重启后的车这一半是对的；车载端自己把一个被拒动作的身份留了下来，之后换了动作还拿它发。**

## 现象

`ros-001` 的 8 条判据：01～04 `PASS`，05 `FAIL`，06～08 未到达。

1. 装载以 `UNKNOWN` 结束，服务端判 `RecoveryRequired`（01）。
2. 维护人员在车上按「申请恢复」：服务端开出恢复会话 `49264f0e…`，`RESUME_AFTER_REPAIR` 因没有已证实的物理断点被拒（`PROVEN_RECOVERY_CHECKPOINT_REQUIRED`），会话留在 `OPEN`（02、03）。
3. 会话开着时重启车载端：车在第 2 代会话里报恢复状态，服务端把那份没确认的 `OPEN` 快照原样重放给它（04）。
4. 重启后按「补偿清空」：弹「补偿清空失败」；再按一次，同样失败（05）。服务端收件箱里始终没有补偿动作。

服务端 `logs/control-server.out.log` 在两次按下时各有一次：

```
Onboard connection ended with a protocol or transport error.
ControlServer.Domain.ProtocolContentConflictException: MessageId was replayed with different normalized content.
   at ControlServer.Infrastructure.Persistence.WireToGateStore.CaptureFirstResponseAsync(...)
   at ControlServer.Host.Transport.OnboardMessageProcessor.ProcessAsync(...) in ...\OnboardMessageProcessor.cs:line 107
```

车载端 `logs/onboard-app/agv-20260914.log`：

```
16:57:28 Warning 恢复申请未执行：session=49264f0e-…，reason=PROVEN_RECOVERY_CHECKPOINT_REQUIRED。
16:57:32 Warning 恢复向量LOAD_COMPENSATION未执行：reason=ControlServer在旅程会话期间关闭了连接。 | EndOfStreamException
16:57:45 Warning 恢复向量LOAD_COMPENSATION未执行：reason=ControlServer在旅程会话期间关闭了连接。 | EndOfStreamException
```

## 原因

- 「申请恢复」被拒之后，车载端日志库 `WireToGateRecoveryState` 里仍留着这次被拒动作的 `recoveryActionId` 与 `recoveryActionRequestId`（`8fc8dbaf-…`）。
  恢复向量那条路被拒时有 `ClearRejectedRecoveryActionVectorAsync` 清理，「申请恢复」这条路没有。
- 「补偿清空」取动作 id 用的是 `state.RecoveryActionId ?? Guid.NewGuid()`（`WireToGateBusinessService.cs:322` 同一写法），于是用同一个 id、同一个 messageId 发出一份内容不同的 `RecoveryActionSubmitted`（动作换成了补偿）。
- 服务端收件箱对「同号不同内容」抛 `ProtocolContentConflictException` 并断开连接；车载端只看到连接被关。
- 那次失败的补偿又把 `LOAD_COMPENSATION/8fc8dbaf-…` 写进车载端日志库，之后每按一次都撞同一个号：**这辆车的补偿入口从此永久不可用，重启也没用。**

按代码推断，不重启也会撞（两个入口都是现读日志库），重启后再按「申请恢复」也会撞（同号，但会话代次与发送时间变了，内容哈希不同）。
现场会走到这一格：维护人员先按「申请恢复」被拒是常见操作。

## 与 MVP 线的关系

MVP 线车载端 `ab346ed`（2026-09-11，「fix(w2g): 恢复请求每次发送都用新 messageId，被拒过的 attempt 能再开恢复会话」）修过同一个问题，在 `OnboardHmi_MVP` 上，**不在 v2 线上**。
同类的 `a696add`、`86fe0a4`（恢复会话快照只确认 CLOSED）与服务端 `219b033`（恢复结果记下后结算对应的恢复命令）也都只在 MVP 线。
两条线 2026-09-04 分叉后，MVP 线的现场修复基本没有带进 v2 线；用户 2026-09-14 裁定另开同步总票逐条核对。

## 修复

- **车载端（用户 2026-09-14 裁定）**：`ab346ed` 移植为 `w2g/b3-on-v2@372186f`。`src/` 两个文件与原提交逐行相同：
  会话请求没有开着的会话时每次按下生成新 requestId；恢复动作的 `recoveryActionId` 跨按下保留（服务端按它去重、被拒不落行），
  messageId 每次发送新生成；补偿清空申请同理。与原提交的差异只在测试侧（v2 没有自动化面恢复端点，两条测试放进 `RecoveryVectorG2Tests`，
  替身照原提交把恢复请求的 messageId 绑到首次到达的整行字节）。
  - 修复前（只放测试）：`RecoveryCanBeRequestedAgainAfterTheServerRefusedTheSession`、`ARequestIdTheServerAlreadyHoldsIsNotSentAgain` 两条红，冲突的正是被复用的 messageId。
  - 修复后：同批 7/7，车载端单元 200/200，G2 全量 56/56，`dotnet format` 0；十片 `ONBOARD_HMI_G2` 全 `PASS`（`FP-IS-07` 23 → 25）。
- 场景随修复调整了一处判据写法（`8bf65925`）：修复后被拒的 `RESUME_AFTER_REPAIR` 与之后的补偿带同一个 `recoveryActionId`、不同 messageId，
  L2-ROS-06 改为按「id + 动作」挑补偿动作，并要求同 id 的其它动作只能是重启前被拒的那次。判据没有放松。
- 服务端可选配套（未做，未定）：同号不同内容时回协议错误而不是断开连接。只让失败不那么粗暴，本身不修复问题。
- MVP 线其余没带进 v2 的修复（含同源的 `a696add`、`86fe0a4`、服务端 `219b033`）归 `8005-agv-program#61` 逐条处理。

## 证据

| 项 | 位置 |
| --- | --- |
| L2 红基线 `ros-001` | `evidence/l2/20260914-real-onboard-restart-with-open-recovery-session-001/`（`SUMMARY.md`、`assertions.json`、`timeline.jsonl`、各组件日志与库快照） |
| 修复后调试 `ros-002`／`ros-003`（未入库） | `ros-002`（服务端 `c261e8e6`，车载端 `372186f`）7/8，只 ROS-06 因场景写法挑出两行；`ros-003` 带判据修正在工作树上 8/8 |
| **L2 绿 `ros-004`** | `evidence/l2/20260914-real-onboard-restart-with-open-recovery-session-004/`：服务端 `8bf65925`、车载端 `19a740c`，均为已提交的干净工作树，**8/8 PASS**——重启后恢复入口可用、没有再开会话、补偿走到对账、车回到 Ready、开着的快照都没被确认 |
| 场景 | `scripts/l2/scenarios/real-onboard-restart-with-open-recovery-session.ps1`（不属于任何 G3 片） |
| 运行时的库 | 本机 `C:\Users\szy\AppData\Local\Temp\l2-20260914T085457826Z`，未入库 |
