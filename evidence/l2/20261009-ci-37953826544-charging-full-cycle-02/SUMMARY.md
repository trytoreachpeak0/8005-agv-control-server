# L2 场景证据：charging-full-cycle

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161027590Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161027590Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 充电单完成、车在 211 上报 CHARGING：211 由预占转为这一趟的占用，周期进 CHARGING（或已涨满到 COMPLETE），记下了到桩时刻 | PASS | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z (arrived)` | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z (arrived '2026-10-09 16:10:54.1626711+00:00')` |
| 电量涨过完成阈值 80：周期 COMPLETE，CHARGING 用途释放（原因 CHARGING_COMPLETE），充电旅程以 CHARGING_COMPLETE 收尾；211 仍是这辆车的占用 | PASS | `COMPLETE \| claim (none) \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z \| journey Completed CHARGING_COMPLETE \| released CHARGING_COMPLETE / battery >= 80` | `COMPLETE \| claim (none) \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z \| journey Completed CHARGING_COMPLETE \| released CHARGING_COMPLETE / battery 80` |
| 充满之后没有新的需求：服务端不为离桩建任何单（RIoT 上仍只有那一张充电单），211 仍被这辆车占着 | PASS | `W2G-CHARGE-BROKERX-L2-0001-20261009T161052423Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z` | `W2G-CHARGE-BROKERX-L2-0001-20261009T161052423Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z` |
| 需求派给了充满的这辆车（仍报 CHARGING），开往取货站的单已确认、假 RIoT 在它队首插了 act(78,2,0)；从下达起另等十秒，211 一直是充电那一趟的占用、周期一直是 COMPLETE（下达下一单不释放） | PASS | `BROKERX-L2-0001 / act 78 2 0\|- / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z \| COMPLETE` | `BROKERX-L2-0001 / act 78 2 - / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161052423Z \| COMPLETE` |
| 车离开 211、不再充电、桩可确认空闲之后：211 的独占释放（原因 CHARGER_RELEASED_ON_DEPARTURE），周期以 CHARGING_DEPARTED 收尾、回 NOT_CHARGING | PASS | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12` | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
