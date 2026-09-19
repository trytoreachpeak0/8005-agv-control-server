# L2 场景证据：real-onboard-cancellation-authorization-lost

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T130951506Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `76c2ca2163832d5be85c83bcfc4f9261aa4db0c7` |
| onboardHmiCommit | `44b3aa6e255f0820c3988d3f50f0b4dba1105006` |
| protocolFaultProxy | `True` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T130951506Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则丢应答注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 出厂配置（recoveryResumeEnabled=false）下装货进行中，车载端给出可用的「取消装货」入口（onboard-hmi#78） | PASS | `True` | `True` |
| 服务端授权了第一次取消、工作流在等结果，授权应答被代理丢掉（车载端提示框只记日志，不作判据） | PASS | `1 条请求 / AUTHORIZED / AwaitingResult / 丢 1 条` | `1 条请求 / AUTHORIZED / AwaitingResult / 丢 1 条（提示框出现=True）` |
| 授权应答丢了之后再按一次，车载端拿到授权并把取消做完、报回 LoadCancellationResult（没有再报失败） | PASS | `LoadCancellationResult` | `LoadCancellationResult ALL_EMPTY → DurableAck` |
| 取消收敛：ALL_EMPTY，工作流 Reconciled，需求与装货 Cancelled，旅程以 CANCELLED_BY_OPERATOR 收尾 | PASS | `ALL_EMPTY / Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR` | `ALL_EMPTY / Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR` |
| 两次按下是两条 messageId 不同的取消请求，payload 相同（同一 cancellationId，沿用首发的操作员与理由），两次都拿到 AUTHORIZED（onboard-hmi#71） | PASS | `2 条 / 2 个 id / payload 相同 / AUTHORIZED,AUTHORIZED` | `2 条 / 2 个 id / payload 相同 / AUTHORIZED,AUTHORIZED` |
| 从第一次按下到收尾，车载端没有重连：丢一次应答不换来一次掐连接 | PASS | `1 connection(s), 1 open` | `1 connection(s), 1 open (#1: open)` |
| 现场收在安全状态：两仓都门关、仓空、已锁、开锁输出复位 | PASS | `1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` | `1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` |
| 从第一次按下到收尾，车没有替这次 attempt 报 OperationResult，服务端一次都没回 RECOVERY_REQUIRED | PASS | `OperationResult 0 条 / RECOVERY_REQUIRED 0 次` | `OperationResult 0 条 / RECOVERY_REQUIRED 0 次` |
| 取消完成也把车还回去：这一单 TO_PICKUP 的车辆占用已释放（VehicleOccupancyReleasedAt 有值），同一台车能再派单 | PASS | `VehicleOccupancyReleasedAt 有值` | `VehicleOccupancyReleasedAt 2026-09-19 13:10:24.9120199+00:00` |
| 一次只开一扇（REQ-0357，onboard-hmi#106）：从第 1 仓开着起到取消收尾，模拟器每 100 ms 的采样里任一时刻至多一仓未锁闭；接手的第 2 仓在取消接手后多开 2 秒 | PASS | `至多 1 仓 / 采样错误 0 次 / 见过 [2] / 接手后见过 1` | `至多 1 仓 / 采样错误 0 次 / 见过 [2]=True / 接手后见过 1=True / [1] → [] → [2] → [] → [1] → []` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
