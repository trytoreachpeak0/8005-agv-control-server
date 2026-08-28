# ControlServer 建单 dispatch 开关：实现、G2 与本机部署

## 背景

`21f1dd6` 依据 BC-ORDER-004 的 upperId 幂等契约取消了一次性 permit，使精确
absent-at-observation 读取可以直接建单。这同时移除了一个操作层面的联锁：在 `21f1dd6` 上，
`JourneyRuntime:enabled=true` 加真实 RIoT 凭据就足以自动 POST 建单，而此前默认关闭的实验门
意味着误开 runtime 也建不了单。开启 runtime 与授权真实下单是两个不同的决定，本次把它们拆成
两个独立开关。

## 变更

- 产品：`ControlServer_MVP@1a0158c87c36fb2e3e0f4ddca1f7ed9c84d5672b`
- 文档：`ControlServer_MVP@d9016e2`（仅 runbook，不改变任何部署字节）

`RiotCreateDispatch:enabled` 默认关闭，缺失该配置段解析为 `RiotCreateDispatchPolicy.Denied`。
所有建单路径都经过 `CreateAfterConfirmedAbsenceAsync` 或 `CreateAfterExperimentalAbsenceAsync`，
两者都在 arm 之前返回 `MovementDispatchOutcome.CreateDispatchDisabled` 且**不写任何东西**：
审计链、intent 状态与 at-most-once 计数器保持原样，因此关闭态下的演练不消耗建单资格，
`JourneyRuntimeEngine` 只把 `BlockReasonCode` 记为 `CreateDispatchDisabled`。

持久 `DispatchAuditVersion == 1` 与 `CreateAttemptCount == 0` 守卫不变，单个 intent 至多一次
建单尝试。本开关只收窄允许建单的时机，不放宽任何既有约束。

## 门禁结果

- Release 全解构建 0 warning / 0 error
- `dotnet format --verify-no-changes` PASS
- 完整 Release 测试 208/208 PASS，0 skipped（较 `21f1dd6` 的 199 新增 9 条）
- 正式 `protocol-v0.1.1` / manifest `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
  下 W2G-IS-00～07 八份 G2 全部 PASS，均绑定 `1a0158c`
- 八份 `gate-result.json` 的排序路径/文件哈希集合 SHA-256：
  `5a3d5f075ec9745ec9c99e5809b68331f0ac88bae78f6336b6aba77a739e1f1d`
  （本机忽略目录 `artifacts/g2/issue10-1a0158c-w2g-is-*/`）

新增测试覆盖：关闭态拒绝精确 absent 建单且零写入、关闭态拒绝 NotFound 建单、关闭态拒绝
实验 permit 建单、连续两次关闭态演练后开关打开仍能正好建单一次，以及 Host 配置缺失该段时
解析为 Denied。

## 本机部署

- package manifest SHA-256：`3475c33e1962eafb0e076afb94bea496af8730a0551dd990c1fe822005f84c4b`
- 升级结果：`PASS`，runId `20260828T155432Z`
- 回滚备份：`C:\ProgramData\8005\ControlServer-backups\20260828T155432Z-upgrade`
- 已安装 `ControlServer.Host.dll` ProductVersion：`1.0.0+1a0158c87c36fb2e3e0f4ddca1f7ed9c84d5672b`
- 已安装有效态：`RiotCreateDispatch:enabled=false`（Production 无覆盖）、
  `JourneyRuntime:enabled=false`、`RiotAbsentAtObservationCreateExperiment:enabled=false`
- 只读安全投影：HTTP 200，`motionState=STOPPED`，`source=RIOT_BEHAVIOR_LAB_R41`，0 reason
- 服务 Running，58005/58007 仅由服务进程持有，58006 无监听
- `riotMutationPerformed=false`、`orderCreated=false`、`vehicleMoved=false`

本次未启用 JourneyRuntime、未启动 Onboard 或 simulator、未访问真实 RIoT、未建单、未移动车辆。

## 未证明的事项

本增量只建立操作联锁与其取证，不构成任何切片的 G3。真实 MesIngest/RIoT 凭据下的完整旅程、
现场物理安全确认和单次真实建单授权仍未取得，W2G-IS-00～07 的正式 G3 与 RC 保持
`INCONCLUSIVE`。现场执行顺序见 `docs/authorized-absent-observation-manual-runbook.md`。
