# L2 场景证据：real-onboard-manual-charging-hold-return

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T141427625Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `65a698c27ea263782e7b16805ff8c1be58d74e19` |
| onboardHmiCommit | `4e40e196205d55c363f97850d10bd18df9a686c0` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37013472279-1\_stage\l2-20261002T141427625Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 空名册、电量 20：服务端置人工充电等待（ROSTER_EMPTY），manualChargingHold=true 的业务状态被真车载端确认，界面充电那一格写「需人工充电：服务端保持」 | PASS | `ROSTER_EMPTY \| True \| True` | `ROSTER_EMPTY \| True \| True (text '需人工充电：服务端保持')` |
| 电量回到 80、服务端看到了一条需求并以人工充电等待为由不派（积压原因 VEHICLE_IN_MANUAL_CHARGING_HOLD）之后十秒里，每次采样都是：服务端的等待仍在、界面仍写着「需人工充电：服务端保持」、积压原因仍是这一条、没有任何订单意图——电量回升本身不解除，等待期间派车轮在轮内拒绝它 | PASS | `ROSTER_EMPTY \| True \| backlog VEHICLE_IN_MANUAL_CHARGING_HOLD \| intents 0` | `ROSTER_EMPTY \| True \| backlog VEHICLE_IN_MANUAL_CHARGING_HOLD \| intents 0` |
| 管理员点「充电后返回服务」并确认：服务端受理为 RETURNED_TO_ELIGIBILITY_EVALUATION，等待删掉、经过上写着解除它的那个请求号；manualChargingHold=false 的业务状态被确认，界面不再写等待 | PASS | `RETURNED_TO_ELIGIBILITY_EVALUATION / (none) \| released dfca7de3-d035-4918-bec3-fc77cc8809cc \| False \| False` | `RETURNED_TO_ELIGIBILITY_EVALUATION / (none) \| released dfca7de3-d035-4918-bec3-fc77cc8809cc \| False \| False` |
| 解除之后车重新可派：等待期间没派出去的那条需求派给了这辆车，开往取货站的单已确认 | PASS | `CONFIRMED BROKERX-L2-0001` | `CONFIRMED BROKERX-L2-0001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
