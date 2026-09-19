# L2 场景证据：real-onboard-load-door-closed-empty-reopens

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T130436721Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `76c2ca2163832d5be85c83bcfc4f9261aa4db0c7` |
| onboardHmiCommit | `44b3aa6e255f0820c3988d3f50f0b4dba1105006` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T130436721Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端说在等操作员时，1 号仓门确实开着、空着、开锁输出已复位 | PASS | `OPEN/EMPTY/0/0` | `OPEN/EMPTY/0/0` |
| 期限之前空关：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、开锁输出复位）、再次等操作员 | PASS | `UNLOCKING 1→2 / WAITING_OPERATOR 增加 / OPEN/EMPTY/0/0 / 重开早于期限` | `UNLOCKING 1→2 / WAITING_OPERATOR 1→2 / OPEN/EMPTY/0/0 / 重开于期限前 37.2 s` |
| 期限已过：车载端倒计时控件（AutomationId StationDepartureCountdown）的状态是 Expired——车载端自己也认定过期了 | PASS | `Expired` | `Expired` |
| 期限之后第 1 次空关：车载端照样自己重开（UNLOCKING +1、门又弹开、再次等操作员），重开发生在期限之后 | PASS | `UNLOCKING 2→3 / WAITING_OPERATOR 增加 / OPEN/EMPTY/0/0 / 重开晚于期限` | `UNLOCKING 2→3 / WAITING_OPERATOR 2→3 / OPEN/EMPTY/0/0 / 重开于期限后 1.7 s` |
| 期限之后第 2 次空关：车载端照样自己重开（UNLOCKING +1、门又弹开、再次等操作员），重开发生在期限之后 | PASS | `UNLOCKING 3→4 / WAITING_OPERATOR 增加 / OPEN/EMPTY/0/0 / 重开晚于期限` | `UNLOCKING 3→4 / WAITING_OPERATOR 3→4 / OPEN/EMPTY/0/0 / 重开于期限后 3.2 s` |
| 期限后空关两次之后：需求未被取消（Accepted），旅程仍 AwaitingLoadResult，装货仍在途（Prepared），车载端一份结果都没报，没有任何 FAILED／OPERATOR_TIMEOUT | PASS | `Accepted / AwaitingLoadResult / Prepared / 0 result / FAILED 结果 0 / OPERATOR_TIMEOUT 0 / Failed 或 RecoveryRequired 操作 0` | `Accepted / AwaitingLoadResult / Prepared / 0 result / FAILED 结果 0 / OPERATOR_TIMEOUT 0 / Failed 或 RecoveryRequired 操作 0` |
| 出厂配置下按取消：车载端发出针对这笔装货的取消请求，服务端授权（AUTHORIZED），范围就是这个仓 | PASS | `a6ddb585-b978-d95a-aa44-239545f3baae / LoadCancellationAuthorization AUTHORIZED [1]` | `a6ddb585-b978-d95a-aa44-239545f3baae / LoadCancellationAuthorization AUTHORIZED [1]` |
| 取消收敛：需求 Cancelled，旅程以 CANCELLED_BY_OPERATOR 结束 | PASS | `Cancelled / Completed / CANCELLED_BY_OPERATOR` | `Cancelled / Completed / CANCELLED_BY_OPERATOR` |
| 目标仓 1 空着、门关、锁上、开锁输出复位 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 车辆释放：调度租约与车辆占用都释放，没有去关卡的单 | PASS | `租约已释放 / 占用已释放 / TO_GATE 0` | `ReleasedAt='2026-09-19 13:05:41.1651958+00:00' / VehicleOccupancyReleasedAt='2026-09-19 13:05:41.1651958+00:00' / TO_GATE 0` |
| 全程没有 FAILED：装货没有被结算成 Failed 或 RecoveryRequired，车载端没报过 FAILED／OPERATOR_TIMEOUT，需求没有完成记录 | PASS | `FAILED 结果 0 / OPERATOR_TIMEOUT 0 / Failed 或 RecoveryRequired 操作 0 / 装货非 Failed、RecoveryRequired、Committed / 完成记录 0` | `FAILED 结果 0 / OPERATOR_TIMEOUT 0 / Failed 或 RecoveryRequired 操作 0 / 装货 Cancelled / 完成记录 0` |
| 取消收尾之后，同一台车在 60 秒内接了下一单（车辆真的被释放了） | PASS | `下一单 AwaitingPickupArrival on AGV-L2-001` | `下一单 AwaitingPickupArrival  on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
