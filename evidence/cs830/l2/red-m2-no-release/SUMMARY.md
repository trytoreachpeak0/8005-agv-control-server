# L2 场景证据：load-remaining-baskets-skipped

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T165648131Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `847f7c39330fe57469d6541d9b350db901d2ecec` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `6.0.0` |
| protocolReleaseIdentity.tag | `protocol-v6.0.0` |
| protocolReleaseIdentity.commit | `98f172642937994ed01668d81fe41e69cf089e10` |
| protocolReleaseIdentity.protocolVersion | `7` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a52876be2ddcc9b8cdd1f1e3b7b7ddbe70d31a307f1d3f017d7a1eb95ffd77b1` |
| protocolReleaseIdentity.schemaBundleSha256 | `0dab682174e2d3e4f931b3c0e87c7fa55bd2cb3395356d7845e595d8bfb1c795` |
| protocolReleaseIdentity.vectorsSha256 | `e06ca9ea1d4445cd7d70fc190b0cf606a8e33cb7760d848c0270df24e2594c91` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T165648131Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 三篮的需求分到三个目标仓，装货命令压在车上（Prepared） | PASS | `3 个目标仓 / Prepared` | `[1,2,3] / Prepared` |
| 服务端授权减篮：答复 AUTHORIZED，loadedSlots 就是装进的两个仓，没有 problem（AUTHORIZE_SKIP_ONLY_WITH_AT_LEAST_ONE_LOADED_AND_ONE_REMAINING_SLOT） | PASS | `AUTHORIZED [1,2]` | `AUTHORIZED [1,2] x1` |
| 车按 COMPLETED_REMAINING_SKIPPED 报完，装货按两篮提交：操作 Committed、操作与归属行都只剩那两个仓（COMMIT_AUTHORIZED_LOADED_SLOTS_AS_ONE_LOAD_BATCH、RELEASE_REMAINING_SLOTS_AND_RESERVATION_ON_SETTLEMENT） | FAIL | `Committed / [1,2] / [1,2] / 车已报结果` | `Committed / [1,2] / [1,2,3] / resultReported=True` |
| 卸货命令只开那两个仓，expectedBasketCount 2（UNLOAD_ONLY_THE_AUTHORIZED_LOADED_SLOTS） | FAIL | `UNLOAD [1,2] x2` | `UNLOAD [1,2,3] x3` |
| 卸完按正常送达完成：需求 Succeeded、业务键记完成，没有抑制、没有人工搬运告警（COMPLETE_DEMAND_AS_NORMAL_DELIVERY_WITHOUT_SUPPRESSION_OR_ALARM） | PASS | `Succeeded / 1 completion / 0 suppression / 0 alarm` | `Succeeded / 1 completion / 0 suppression / 0 alarm` |
| 全程只建了两条 RIoT 单（取货一条、关卡一条） | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
