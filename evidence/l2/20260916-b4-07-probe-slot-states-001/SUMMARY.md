# L2 场景证据：b4-07-probe-slot-states

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260916T105644860Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `0bff08c7b23024262dbf33d0c5e9f582bbd1324b` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| protocolReleaseIdentity.schemaBundleSha256 | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260916T105644860Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端就当前会话算出的可用仓是 3～8（1 号 OCCUPIED、2 号 DISABLED 被排除） | PASS | `3,4,5,6,7,8` | `3,4,5,6,7,8` |
| 服务端收件箱里的 CapabilitySnapshot 报的正是种子：1 号 OCCUPIED/ENABLED，2 号 EMPTY/DISABLED | PASS | `1:OCCUPIED/ENABLED;2:EMPTY/DISABLED` | `1:OCCUPIED/ENABLED;2:EMPTY/DISABLED` |
| 受理的需求目标仓是 [3,4]：可用仓减少直接体现在派车上（默认种子下是 [1,2]） | PASS | `[3,4]` | `[3,4]` |
| 从 SlotModelSlots 经该车最新已发布绑定读到的分组：1～4 FRONT、5～8 REAR（读出来的，不是写死的） | PASS | `LatestPublishedIoBinding: 1=FRONT,2=FRONT,3=FRONT,4=FRONT,5=REAR,6=REAR,7=REAR,8=REAR` | `LatestPublishedIoBinding: 1=FRONT,2=FRONT,3=FRONT,4=FRONT,5=REAR,6=REAR,7=REAR,8=REAR` |
| 正例：目标仓 [3,4] 全属 FRONT、升序、恰是 FRONT 组最小的两个可用仓 | PASS | `[3,4]（FRONT 组最小的可用仓，升序）` | `[3,4] on AGV-L2-001` |
| 反例：同一需求按 REAR 断言判失败，并说明是哪一条不成立 | PASS | `FAIL: not in group REAR` | `FAIL: slot(s) 3, 4 are not in group REAR` |
| 读 JourneyBacklog 与 StructuralDispatchBlocks 的辅助函数可用：积压行 ACCEPTED，结构性阻塞 0 行 | PASS | `ACCEPTED / 0` | `ACCEPTED / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
