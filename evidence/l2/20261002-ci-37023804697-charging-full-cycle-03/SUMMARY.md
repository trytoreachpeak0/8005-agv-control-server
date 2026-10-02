# L2 场景证据：charging-full-cycle

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T152754361Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `6b7b3e38b7d72b454b227a104de2e37a3198db86` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T152754361Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 充电单完成、车在 211 上报 CHARGING：211 由预占转为这一趟的占用，周期进 CHARGING（或已涨满到 COMPLETE），记下了到桩时刻 | PASS | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z (arrived)` | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z (arrived '2026-10-02 15:28:20.5625223+00:00')` |
| 电量涨过完成阈值 80：周期 COMPLETE，CHARGING 用途释放（原因 CHARGING_COMPLETE），充电旅程以 CHARGING_COMPLETE 收尾；211 仍是这辆车的占用 | PASS | `COMPLETE \| claim (none) \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z \| journey Completed CHARGING_COMPLETE \| released CHARGING_COMPLETE / battery >= 80` | `COMPLETE \| claim (none) \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z \| journey Completed CHARGING_COMPLETE \| released CHARGING_COMPLETE / battery 80` |
| 充满之后没有新的需求：服务端不为离桩建任何单（RIoT 上仍只有那一张充电单），211 仍被这辆车占着 | PASS | `W2G-CHARGE-BROKERX-L2-0001-20261002T152818617Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z` | `W2G-CHARGE-BROKERX-L2-0001-20261002T152818617Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z` |
| 需求派给了充满的这辆车（仍报 CHARGING），开往取货站的单已确认、假 RIoT 在它队首插了 act(78,2,0)；从下达起另等十秒，211 一直是充电那一趟的占用、周期一直是 COMPLETE（下达下一单不释放） | PASS | `BROKERX-L2-0001 / act 78 2 0\|- / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z \| COMPLETE` | `BROKERX-L2-0001 / act 78 2 - / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261002T152818617Z \| COMPLETE` |
| 车离开 211、不再充电、桩可确认空闲之后：211 的独占释放（原因 CHARGER_RELEASED_ON_DEPARTURE），周期以 CHARGING_DEPARTED 收尾、回 NOT_CHARGING | PASS | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12` | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
