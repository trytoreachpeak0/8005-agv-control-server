# 缺陷：onboard-hmi#109 合入后，车载端重启进入 `RecoveryRequired` 时四个管理员恢复入口都不出现

Status: open（车载端回归），修复票 [onboard-hmi#112](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/112)
Owner repository: `8005-agv-onboard-hmi`（`w2g/fp-v2-impl`，onboard-hmi PR #111 的合并提交 `9748c418` 引入；`8f308bb1` 上没有）
Found by: control-server#90（批次 5 出口）第 4 步 `run-journey-g3.ps1` 与第 6 步真装置 L2，2026-09-19：
- [`evidence/g3/20260918-protocol-v2.0.0-journey-06b65688/`](../../evidence/g3/20260918-protocol-v2.0.0-journey-06b65688/)：`JOURNEY_G3_SLICE_FAIL`，`FP-IS-07` 四条恢复场景红
- [`evidence/l2/20260919-real-onboard-compensate-then-reconnect-001/`](../../evidence/l2/20260919-real-onboard-compensate-then-reconnect-001/)：`L2-CR-02`～`08` 未到达
- 对照：[`evidence/l2/20260919-real-onboard-compensate-then-reconnect-onboard-8f308bb1-001/`](../../evidence/l2/20260919-real-onboard-compensate-then-reconnect-onboard-8f308bb1-001/)
Product at discovery: control-server `e0f26b37`（harness `06b65688`）；onboard-hmi `9748c418`；slots-simulator `fb5f7c59`；协议 `protocol-v2.0.0@86575456`

## 现象

车载端在装货途中被杀、重启后把那一仓的中断操作按实时 IO 结算成 `UNKNOWN`，会话以 `RecoveryRequired` 建立（车载端日志：
`上层会话已建立：generation=2，readiness=RecoveryRequired`），服务端旅程转 `Blocked`，告警 `ONBOARD_SLOT_OPERATION_UNFINISHED` 已上报。
到这里都符合预期。此后应当出现的管理员入口一个都没有出现：

| 场景 | 等待的入口 | 结果 |
| --- | --- | --- |
| `g3-exception-resume`（journey） | 申请恢复 | `(not reached) 重启后车载端没有给出「申请恢复」入口`（`G3-07-01`～`05` PASS，`06` 起未到达） |
| `g3-exception-compensate`（journey） | 补偿清空 | `(not reached) 车载端没有给出「补偿清空」入口`，等了 90 秒 |
| `g3-fault-cargo-handoff`（journey） | 故障交接 | `(not reached) 车载端没有给出「故障交接」入口` |
| `g3-forced-mechanical-recovery`（journey） | 强制机械恢复 | `(not reached) 车载端没有给出「强制机械恢复」入口` |
| `real-onboard-compensate-then-reconnect`（真装置 L2） | 补偿清空 | `L2-CR-00`、`01` PASS（`UNKNOWN / RecoveryRequired / Blocked`），`L2-CR-02`～`08` 未到达，原因同上 |

同一轮 journey 的其余 7 条场景（`FP-IS-01`／`02`／`03`）与另外 6 条真装置 L2 场景全部 PASS，所以会话、执行器、模拟器与编排器本身是好的。

## 红绿对照：入口是在 `8f308bb1..9748c418` 之间消失的

服务端固定在 `06b65688`，只把车载端克隆 detach 到 hmi#109 之前的 `8f308bb1`，重跑 `real-onboard-compensate-then-reconnect`：

```
onboard-compensation-entry   True
Pressing 补偿清空 (attempt 1).
```

入口出现、按下，服务端 0.5 秒内完成整条补偿：恢复会话 `CLOSED`、`SelectedAction = COMPENSATE_LOAD_ALL_EMPTY`，工作流 `Reconciled`、`Outcome = ALL_EMPTY`
（见对照目录 `snapshots/db-ExceptionRecoverySessions.json`、`db-RecoveryWorkflows.json`）。

在 `9748c418` 上同样的步骤，5 次运行（journey 四条＋真装置一条）5 次入口不出现。

再往前的参照：control-server#128 合入前（2026-09-18 10:20Z），五条恢复类场景在车载端 `8153946b`、服务端 `10f43ab4` 上各一次 PASS（PR #136 正文）。

**结论：这是 onboard-hmi#109（PR #111，`9748c418`）带进来的车载端回归。**`8f308bb1..9748c418` 只有 PR #111 一个合并。

> 对照那一次最后仍判 FAIL：`Timed out after 120s waiting for: the server received LoadCompensationResult, or rejected the compensation request.`
> 服务端库里补偿已经完整收敛，是场景观察「服务端收到补偿结果」的那一步没看到东西。它发生在新服务端配旧车载端的混搭上，不在本缺陷的路径上，
> 只在这里记下；修复后在 `9748c418` 的后继上重跑时，若它再出现，按场景问题单独处理。

## 读代码能确定的与没能确定的

- 四个按钮的 XAML 绑定没有变：`IsEnabled` 与 `Visibility` 都绑在各自的 `CanRequest*` 上（`MainWindow.xaml`）。WPF 里 `Collapsed` 的元素不进 UIA 树，
  场景按按钮名 `FindFirst(TreeScope.Descendants, Name)` 找不到它，所以现象等价于视图模型里四个 `CanRequest*` 都是 `false`。
- 四个入口共用的前提是：控制器状态不是 `Faulted`（否则 `ApplyWireToGatePresentationCore` 把全部入口置 `false` 后返回），以及业务服务的
  `CanUseRecoveryOperator`（`ResumeAfterRepairEnabled`、操作员编号与恢复凭据两个环境变量、会话 `Ready`／`RecoveryRequired`）和 `CanRequestRecoveryAction`。
  这几处在 `8f308bb1..9748c418` 里没有改动。
- PR #111 在这条路径上改了的：`MainViewModel` 的四个 `CanRequest*` setter 换成 `SetRecoveryEntry`，`ApplyWireToGatePresentationCore` 末尾加了
  `RefreshRecoveryReasonLockCore()`；`App.xaml.cs` 的 `ConfigureWireToGate` 调用改了四个请求委托的签名并加了 `RecoveryReasonAlreadyGiven`；
  `UpdateOnboardAlarms` 与 `UpdateWireToGateStatus` 加了 `RefreshExpectedActionOverdueCore()`；`WireToGateSessionClient` 加了 `SafetyStateSnapshotRequested` 的处理。
  **具体哪一处把入口关掉，本票没有定位到**，留给修复票在车载端用确定性的单元测试定位。

## 修复要做什么（交调度会话开票）

- **所在仓**：`8005-agv-onboard-hmi`，分支从 `w2g/fp-v2-impl` 开，PR 回 `w2g/fp-v2-impl`。
- **先写失败测试**：在视图模型层构造「重启后中断操作结算为 `UNKNOWN`、会话 `RecoveryRequired`、管理员凭据齐全」的状态，断言
  `CanRequestLoadCompensation`（以及申请恢复、故障交接、强制机械恢复）为 `true`；在 `8f308bb1` 上应绿、在 `9748c418` 上应红。若视图模型层复现不了，
  就要在真装置上抓一次重启后窗口的 UIA 树与视图模型状态。
- **怎么证明红变绿**：修复提交上跑 `real-onboard-compensate-then-reconnect` 一次 PASS（`L2-CR-00`～`08`），并在修复前的 `9748c418` 上保留本单的红证据作对照；
  `ONBOARD_HMI_G2` 至少重跑 `FP-IS-07` 与 `FP-IS-15`（PR #111 动过告警与恢复入口）。
- **影响的出口证据**：修复会改车载端产品代码，所以 control-server#90 在修复合入后要：
  - 车载端 `ONBOARD_HMI_G2` 十片在新提交上重跑（证据绑 `implementationCommit`）；
  - G3 共享绑定的 `OnboardCommit` 移到新提交，**四个 G3 runner 全部重跑**（绑定变了，restart 与需求承载两份绿证据也不再绑现行身份）；
  - 真装置七条重跑；
  - 服务端 `CONTROL_SERVER_G2` 与 CI 三连只用合成车载端，不碰车载端代码，证据保留。

## 修复

修在 onboard-hmi PR [#114](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/114)（onboard-hmi#112）：`MainViewModel.SetRecoveryEntry` 转交调用方属性名。
根因：`SetRecoveryEntry` 调用 `SetProperty` 时 `[CallerMemberName]` 取成了 `"SetRecoveryEntry"`，四个 `CanRequest*` 的值是对的，
但 WPF 绑定收不到以它们命名的变更通知，按钮停在启动时的 `Collapsed`。

**更正上文「读代码能确定的与没能确定的」第一条的推断。**那里写「现象等价于视图模型里四个 `CanRequest*` 都是 `false`」，这是错的：
值一直是 `true`，错的是变更通知的属性名。这也是读代码时在「值为什么是 `false`」这条线上找不到原因的缘故。上文保留原样，以本节为准。

PR #114 的新测试 `RecoveryEntryNotificationViewModelTests.AfterARestartSettledAsUnknownTheFourAdministratorEntriesAreAnnouncedToTheWindow`
同时断言属性值与以属性名发出的 `PropertyChanged`：在 `9748c418` 上红（`Not found: "CanRequestWireToGateRecovery"`，收集到的通知名是
`["SetRecoveryEntry", "HasRecoveryReasonInput", ...]`），在 `8f308bb1` 与修复提交上绿。只断言值抓不到这个缺陷：`9748c418` 上四个值断言都过。

**对照那一次的 `Last observed: (nothing)` 也有了解释**（见上文「红绿对照」一节末的引用块）：不是新旧版本混搭的问题，是服务端 L2 脚本的缺陷。
`scripts/l2/L2RealOnboard.psm1` 的 `Get-L2RealInbound` 在只有一条回应时，`$answers` 是单个对象而不是数组，读 `$answers.Count` 抛异常，
场景的等待条件因此一直观察不到「服务端收到补偿结果」。由 onboard-hmi#112 的会话发现，修复票 [control-server#154](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/154)。
所以 `real-onboard-compensate-then-reconnect` 要在 PR #114 与 control-server#154 都合入后才能转绿；本票的重跑以两者都合入为前置。

本单状态在 PR #114 与 control-server#154 合入、control-server#90 在修复后的车载端提交上重跑真装置七条与四个 G3 runner 全绿后改为 fixed。

## 为什么合入前没发现

PR #111 在车载端 CI（`ONBOARD_HMI_G2` 与布局检查）上是绿的，这两道都不启动真 WPF 窗口去驱动恢复入口。恢复入口的端到端覆盖只在真装置 L2 与 journey G3 里，
它们要本机真装置时段，按批次 5 的安排集中在出口票跑，所以出口这一轮是 PR #111 之后第一次在真车载端上走恢复路径。
