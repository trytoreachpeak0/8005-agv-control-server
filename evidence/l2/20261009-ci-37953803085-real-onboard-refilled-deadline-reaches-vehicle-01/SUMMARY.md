# L2 场景证据：real-onboard-refilled-deadline-reaches-vehicle

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161912260Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
| protocolFaultProxy | `True` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37953803085-1\_stage\l2-20261009T161912260Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：到站之后，车载端采纳的清单就是到站那一版，期限与服务端一致（到站起点加 5 分钟） | PASS | `r1 / 2026-10-09T16:24:51.9004984+00:00` | `r1 / 2026-10-09T16:24:51.9004984+00:00` |
| 断开一次之后会话在新的一代回到 Ready | PASS | `gen > 1 / Ready` | `gen 2 / Ready / READY` |
| 服务端按 ADR-cross-0055 重填了期限：起点换成重连之后的时刻 | PASS | `> 2026-10-09T16:19:51.9004984+00:00` | `2026-10-09T16:20:06.0552240+00:00` |
| 车载端采纳的清单期限等于服务端此刻判定用的期限，并且不是到站那一个 | PASS | `车上 = 服务端 ≠ 到站那一个 2026-10-09T16:24:51.9004984+00:00（第一次重填是 2026-10-09T16:25:06.0552240+00:00）` | `车上 r2 2026-10-09T16:25:06.0552240+00:00 / 服务端 2026-10-09T16:25:06.0552240+00:00` |
| 车载端采纳的是一版号更大的清单：重填作为新的一版下发，不是同号改内容 | PASS | `r > 1` | `r2` |
| 服务端最后发出的录入请求答的是车手上那一版清单，旅程仍在等录入 | PASS | `录入请求 r2 / AwaitingSublot` | `录入请求 r2 / AwaitingSublot` |
| 操作员看到的离站倒计时就是服务端的期限（容差 2 秒），不是到站那一个 | PASS | `约 300 秒（到站那一个此刻约 286 秒）` | `「05:00」于 2026-10-09T16:20:06.7752825+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
