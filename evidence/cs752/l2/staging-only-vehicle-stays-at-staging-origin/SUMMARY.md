# L2 场景证据：staging-only-vehicle-stays-at-staging-origin

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T140050803Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `219687e70afad4f4f097b7e828a0a3a6762dd536` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `4e9f3df8fb1733311e1ce534c1129cfb904760a4` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `743b886d99194038b893e98aed2a1dd01409cd7f80d39c734e02d860e0e5827c` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T140050803Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| V 在待送点扫码前取消：旅程 Completed / CANCELLED_BY_OPERATOR，V 停在 305，305 的单占仍是 V 的 | PASS | `CANCELLED_BY_OPERATOR / at 305 / 305 held by BROKERX-L2-0001` | `CANCELLED_BY_OPERATOR / at 305 / 305 held by BROKERX-L2-0001 FIXED_TASK_STATION` |
| V 停在 305 时有一个空着、开得过去的等待点（V 若是从等待点来的，那个点已被离点证据放掉） | PASS | `a free waiting point` | `free=215 former=` |
| V 在 305 停了十几轮：没有 IDLE_RETURN 用途占有、没有新的开往等待点的单，车仍在 305，305 仍是 V 的 | PASS | `no claim / 1 intents / at 305 / held by BROKERX-L2-0001` | `claim  / 1 intents / at 305 / held by BROKERX-L2-0001` |
| 空闲返回看板上 V 这一轮的结论码是 IDLE_RETURN_HELD_AT_STAGING_ORIGIN | PASS | `IDLE_RETURN_HELD_AT_STAGING_ORIGIN` | `IDLE_RETURN_HELD_AT_STAGING_ORIGIN` |
| 第二条待送需求派给已在待送点的 V；开往 305 的取货单在 V 停在 305 时建出 | PASS | `BROKERX-L2-0001 / order to 305 / at 305` | `BROKERX-L2-0001 / order to 305 by BROKERX-L2-0001 / at 305` |
| 取货单完成后 V 在 305 到站，录入请求发到 V 的车上（清单可扫） | PASS | `AwaitingSublot / >= 1` | `AwaitingSublot / 1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
