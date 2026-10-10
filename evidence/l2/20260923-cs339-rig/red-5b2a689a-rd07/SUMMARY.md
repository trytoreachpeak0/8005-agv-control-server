# L2 场景证据：real-onboard-refilled-deadline-reaches-vehicle

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260923T181340252Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `5b2a689a7cd0624082ed1f27c5b640def3b42d9f` |
| onboardHmiCommit | `1184bb0762462f443736de749fe242fc9aa2f0d7` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260923T181340252Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：到站之后，车载端采纳的清单就是到站那一版，期限与服务端一致（到站起点加 5 分钟） | PASS | `r1 / 2026-09-23T18:19:12.7664680+00:00` | `r1 / 2026-09-23T18:19:12.7664680+00:00` |
| 断开一次之后会话在新的一代回到 Ready | PASS | `gen > 1 / Ready` | `gen 2 / Ready / READY` |
| 服务端按 ADR-cross-0055 重填了期限：起点换成重连之后的时刻 | PASS | `> 2026-09-23T18:14:12.7664680+00:00` | `2026-09-23T18:14:26.7050434+00:00` |
| 车载端采纳的清单期限等于服务端此刻判定用的期限，并且不是到站那一个 | FAIL | `车上 = 服务端 ≠ 到站那一个 2026-09-23T18:19:12.7664680+00:00（第一次重填是 2026-09-23T18:19:26.7050434+00:00）` | `车上 r1 2026-09-23T18:19:12.7664680+00:00 / 服务端 2026-09-23T18:19:26.7050434+00:00` |
| 车载端采纳的是一版号更大的清单：重填作为新的一版下发，不是同号改内容 | FAIL | `r > 1` | `r1` |
| 服务端最后发出的录入请求答的是车手上那一版清单，旅程仍在等录入 | PASS | `录入请求 r1 / AwaitingSublot` | `录入请求 r1 / AwaitingSublot` |
| 操作员看到的离站倒计时就是服务端的期限（容差 2 秒），不是到站那一个 | FAIL | `约 225 秒（到站那一个此刻约 211 秒）` | `「03:31」于 2026-09-23T18:15:42.1587041+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
