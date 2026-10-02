# L2 场景证据：waiting-point-exclusive-reserve-occupy-release

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T150259529Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T150259529Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车空闲、没有需求：空闲返回物化成一趟没有需求的旅程，建了开往 214 的单段移动（意图没有需求）；214 是这一趟的在途预占，IDLE_RETURN 用途占有是同一趟 | PASS | `214 RESERVED WAITING_POINT idle-return:BROKERX-L2-0001:20261002T150325719Z, claim IDLE_RETURN` | `RESERVED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20261002T150325719Z; claim IDLE_RETURN idle-return:BROKERX-L2-0001:20261002T150325719Z; intent demand ''` |
| 到点证据全满足：214 转为这一趟的在点占用，IDLE_RETURN 用途占有释放（原因 IDLE_RETURN_CONVERGED_AT_WAITING_POINT），空闲返回旅程无码收尾 | PASS | `214 OCCUPIED idle-return:BROKERX-L2-0001:20261002T150325719Z, no claim, released IDLE_RETURN_CONVERGED_AT_WAITING_POINT` | `OCCUPIED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20261002T150325719Z; claim ; record IDLE_RETURN_CONVERGED_AT_WAITING_POINT` |
| 需求派给了停在 214 的这辆车（收敛之后它回到可选择），开往机台的单已确认 | PASS | `accepted and confirmed` | `AwaitingPickupArrival CONFIRMED` |
| 离点订单已下达、车还在 214：214 仍是那一趟的在点占用（下达离点订单时不释放，REQ-0293） | PASS | `214 OCCUPIED idle-return:BROKERX-L2-0001:20261002T150325719Z` | `OCCUPIED WAITING_POINT BROKERX-L2-0001 idle-return:BROKERX-L2-0001:20261002T150325719Z` |
| 车到了机台（RIoT 报当前站是另一个站）：214 的独占以离点证据释放，预占、占用、释放三个时刻依次记在同一段经过上 | PASS | `released DEPARTED_STATION, reserved <= occupied <= released` | `held: (none); record 2026-10-02 15:03:25.7197275+00:00 / 2026-10-02 15:03:27.4852526+00:00 / 2026-10-02 15:03:40.4325379+00:00 (DEPARTED_STATION)` |
| 卸完没有需求：停在关卡上（持公共站点独占）的车再次承诺空闲返回，开离关卡去 214 后关卡独占凭离点证据释放；这是另一趟空闲返回 | PASS | `gate released DEPARTED_STATION; a second idle return` | `gate held: (none); record DEPARTED_STATION; second idle-return:BROKERX-L2-0001:20261002T150416664Z Completed` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
