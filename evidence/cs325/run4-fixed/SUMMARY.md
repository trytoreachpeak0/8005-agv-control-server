# L2 场景证据：real-onboard-stale-stop-after-station-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260926T174908175Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `fd1d40beba37c3ddc8d04a766caa298fad19ca07` |
| onboardHmiCommit | `4c2d2dc14656f80e812f37128a7964c2c310217a` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260926T174908175Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则第二趟丢不掉那张空清单） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 第一趟：车到站后要子批、给「取消装货」、清单挂着这单（前提，也是下面三条「不在」的正向锚点） | PASS | `canSubmit / offersCancel / listsSublot 均为 True` | `canSubmit=True offersCancel=True listsSublot=True worklist=[L2-SST-A-20260926T174908175Z]` |
| 第一趟：站点期限到，服务端以 CANCELLED_BY_STATION_TIMEOUT 结束需求、旅程 Completed、没发过仓位命令（前提） | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0 条仓位操作` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0 条仓位操作` |
| 第一趟：旅程被站点期限结束之后，车不再要子批 | PASS | `canSubmit=False` | `canSubmit=False offersCancel=False listsSublot=False worklist=[]` |
| 第一趟：旅程被站点期限结束之后，车不再给「取消装货」 | PASS | `offersCancel=False` | `canSubmit=False offersCancel=False listsSublot=False worklist=[]` |
| 第一趟：旅程被站点期限结束之后，车上的清单不再挂着这单 | PASS | `listsSublot=False` | `canSubmit=False offersCancel=False listsSublot=False worklist=[]` |
| 第二趟（前提）：服务端已以站点期限收尾，车上仍要子批、仍给「取消装货」、清单仍挂着这单——迟到的取消只能发生在这个窗口里 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0；canSubmit / offersCancel / listsSublot 均为 True` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0 条仓位操作；canSubmit=True offersCancel=True listsSublot=True worklist=[L2-SST-B-20260926T174908175Z]` |
| 第二趟：收尾之后按「取消装货」（车给几次按几次，最多两下），服务端不掐连接、车不重连 | PASS | `>= 1 下；SessionHello 次数不变（1）` | `按了 1 下（REJECTED/WORKLIST_REVISION_STALE）；按前 1 / 按后 1` |
| 第二趟：迟到的取消每一下都以 WORKLIST_REVISION_STALE 拒绝，不落取消工作流，需求仍是期限判的 Cancelled | PASS | `每一下 REJECTED/WORKLIST_REVISION_STALE / 0 条工作流 / Cancelled` | `REJECTED/WORKLIST_REVISION_STALE；0 条工作流 / Cancelled` |
| 第二趟：迟到的取消被 STALE 拒绝之后，车不再给「取消装货」（车载端据 STALE 认定本站已结束） | PASS | `offersCancel=False` | `canSubmit=False offersCancel=False listsSublot=False worklist=[]` |
| 第三趟（前提）：服务端已以站点期限收尾，车上仍要子批、仍给「取消装货」、清单仍挂着这单——迟到的扫码只能发生在这个窗口里 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0；canSubmit / offersCancel / listsSublot 均为 True` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0 条仓位操作；canSubmit=True offersCancel=True listsSublot=True worklist=[L2-SST-C-20260926T174908175Z]` |
| 第三趟：收尾之后的迟到扫码恰好得到一条 SublotRejected / WORKLIST_REVISION_STALE，经代理送到车上、提示区显示这个原因 | PASS | `1 × WORKLIST_REVISION_STALE delivered=True；提示区 WORKLIST_REVISION_STALE` | `录入 b5368159-018a-48a5-ba09-717afca3678e：WORKLIST_REVISION_STALE delivered=True；提示区 WORKLIST_REVISION_STALE` |
| 第三趟：迟到的扫码被 STALE 拒绝之后，车不再要子批、不再给「取消装货」 | PASS | `canSubmit=False / offersCancel=False` | `canSubmit=False offersCancel=False listsSublot=True worklist=[L2-SST-C-20260926T174908175Z]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
