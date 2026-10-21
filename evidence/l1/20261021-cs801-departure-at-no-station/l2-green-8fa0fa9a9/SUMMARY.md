# L2 场景证据：charging-full-cycle

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T061905612Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `8fa0fa9a9dd68552f2c3732e580e5c35d768d1e0` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T061905612Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 充电单完成、车在 211 上报 CHARGING：211 由预占转为这一趟的占用，周期进 CHARGING（或已涨满到 COMPLETE），记下了到桩时刻 | PASS | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z (arrived)` | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z (arrived '2026-10-21 06:19:33.7323559+00:00')` |
| 电量涨过完成阈值 80：周期 COMPLETE，CHARGING 用途释放（原因 CHARGING_COMPLETE），充电旅程以 CHARGING_COMPLETE 收尾；211 仍是这辆车的占用 | PASS | `COMPLETE \| claim (none) \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z \| journey Completed CHARGING_COMPLETE \| released CHARGING_COMPLETE / battery >= 80` | `COMPLETE \| claim (none) \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z \| journey Completed CHARGING_COMPLETE \| released CHARGING_COMPLETE / battery 80` |
| 充满之后没有新的需求：服务端不为离桩建任何单（RIoT 上仍只有那一张充电单），211 仍被这辆车占着 | PASS | `W2G-CHARGE-09c3783f-20261021T061931772Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z` | `W2G-CHARGE-09c3783f-20261021T061931772Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z` |
| 需求派给了充满的这辆车（仍报 CHARGING），开往取货站的单已确认、假 RIoT 在它队首插了 act(78,2,0)；从下达起另等十秒，211 一直是充电那一趟的占用、周期一直是 COMPLETE（下达下一单不释放） | PASS | `BROKERX-L2-0001 / act 78 2 0\|- / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z \| COMPLETE` | `BROKERX-L2-0001 / act 78 2 - / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z \| COMPLETE` |
| 离桩动作做完、不再充电、RIoT 报 currentPosition 0（不在任何站，车还在桩口）：另等十秒，211 一直是充电那一趟的占用、周期一直是 COMPLETE（control-server#801） | PASS | `0 / NO_CHARGE / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z \| COMPLETE` | `0 / NO_CHARGE / OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T061931772Z \| COMPLETE` |
| 车离开 211、不再充电、桩可确认空闲之后：211 的独占释放（原因 CHARGER_RELEASED_ON_DEPARTURE），周期以 CHARGING_DEPARTED 收尾、回 NOT_CHARGING | PASS | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12` | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
