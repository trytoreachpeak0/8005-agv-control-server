# L2 场景证据：real-onboard-durable-ack-lost

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T143359341Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `07d3ef4e325755d7ca47aacfe8c548d16688e60b` |
| onboardHmiCommit | `24af41e4ab769f10cd15381fd3b4a56babb62c78` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35516706603-1\_stage\l2-20260920T143359341Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则丢 ack 注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 丢掉的是一份服务端已经收下的装货结果：ProtocolInbox 有这一行，装货已 Committed | PASS | `OperationResult / Committed` | `OperationResult / Committed` |
| 车重连后在新会话里以原 messageId 补发 OperationResult、sessionGeneration 换成新的，服务端按首次受理重签 DurableAck（control-server#77） | PASS | `same messageId replayed at a newer generation and acknowledged` | `first on #1 at generation 1; 1 replay(s), first on #2 at generation 2; acked on a later connection 1 time(s)` |
| 补发之后同一条连接照常走完握手：Hello → Accepted → 补发 → 其 ack → 能力快照 → 安全快照 → 新 messageId 的 RecoveryStateReport → SessionReadiness；旧的恢复报告一条都不按原编号补发（onboard-hmi#69，CV-SESSION-RECONNECT-DURING-RECOVERY） | PASS | `按序 / 新报告 >= 1 / 旧报告补发 0` | `#2 onboard->server:SessionHello server->onboard:SessionAccepted onboard->server:OperationResult server->onboard:DurableAck onboard->server:CapabilitySnapshot server->onboard:SnapshotAppliedAck onboard->server:SafetyStateSnapshot server->onboard:SnapshotAppliedAck onboard->server:OnboardAlarmSnapshot server->onboard:SnapshotAppliedAck onboard->server:RecoveryStateReport server->onboard:DurableAck server->onboard:SessionReadiness onboard->server:SafetyStateChanged server->onboard:DurableAck server->onboard:SessionReadiness / 新报告 1 / 旧报告补发 0` |
| 服务端在新世代把会话判回 Ready：本代次能力快照、安全快照与恢复状态报告齐全 | PASS | `gen > 1 / Ready / READY` | `gen 2 / Ready / READY` |
| 确认丢失的那次装货已完成：会话重回 Ready 之后、卸货等操作员之前，HMI 上从未出现「上次装货操作未完成」；开头 10 秒采样窗与之后到卸货等操作员为止**各**至少 10 轮把整棵 UIA 树读全了（onboard-hmi#124，计数与分段在 control-server#204 收紧） | PASS | `采样窗 ≥ 10 轮 / 之后 ≥ 10 轮 / 检出 0 次` | `干净扫描 73 轮（最少一轮读到 6 个元素）/ 失败 0 轮（读失败元素 0 个）/ 检出 0 次（采样窗 21 轮 / 之后 52 轮）` |
| 补发被确认之后旅程照常走完 | PASS | `Completed` | `Completed` |
| 装货结果只记了一次，卸货 Committed，需求 Succeeded | PASS | `1 / Committed / Succeeded` | `1 / Committed / Succeeded` |
| 补发之后没有哪条连接被服务端掐掉：丢 ack 之后的连接要么车自己关的、要么到收尾还开着 | PASS | `0 connections after the drop ended by anyone but the onboard` | `0 (#1: relay dropped DurableAck for OperationResult; #2: onboard closed; #3: open)` |
| 丢一次 ack 只换来一次重连：之后只有一条连接，而且到收尾还开着 | FAIL | `2 connections, 1 open` | `3 connections, 1 open (#1: relay dropped DurableAck for OperationResult; #2: onboard closed; #3: open)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
