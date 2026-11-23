# L2 场景证据：onboard-disconnected-takes-no-new-purpose

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261123T144127439Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `ef60f6cf08c5fc574f2f7a1a000c015deb760af4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `7.0.0` |
| protocolReleaseIdentity.tag | `protocol-v7.0.0` |
| protocolReleaseIdentity.commit | `a1facf8674b05c67bfbddc14db4a163e8da2782a` |
| protocolReleaseIdentity.protocolVersion | `8` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `44fa0b273810fb2d5326068c03f9647bfd8fdc1c5f01e27a5177b0e0473fe7cb` |
| protocolReleaseIdentity.schemaBundleSha256 | `6361d734f9b1a4f81178c79c0bc18b8287fd747622e60c99073e7fd5a5cf4c88` |
| protocolReleaseIdentity.vectorsSha256 | `6295840ec27422dc024a24f6deae9a7379f9b3047a163e903ac862c8b0274eec` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261123T144127439Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 断线前：车载端 READY，库里还没有任何用途占有、旅程或订单意图（车不在任何站上） | PASS | `READY / claims=0 journeys=0 intents=0` | `READY / claims=0 journeys=0 intents=0` |
| 断线后的 20 秒、十几轮派车里没有任何用途占有、旅程或订单意图：空闲返回不承诺、搬运不受理（整段在断线后 30 秒以内，至少转了 3 轮） | PASS | `claims=0 journeys=0 intents=0 / < 30 s / >= 3 rounds` | `claims=0 journeys=0 intents=0 / 20.1 s / 20 rounds` |
| 那条需求留在积压里，原因是车载端事实没有就绪（ONBOARD_FACTS_NOT_READY） | PASS | `ONBOARD_FACTS_NOT_READY` | `ONBOARD_FACTS_NOT_READY` |
| 重连走完完整握手：车载端回到 READY，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 重连就绪后，那条需求派给了这台车 | PASS | `AGV-L2-001` | `AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
