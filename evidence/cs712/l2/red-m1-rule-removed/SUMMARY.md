# L2 场景证据：staging-only-vehicle-stays-at-staging-origin

结论：**FAIL**

失败原因：Timed out after 180s waiting for: second demand reached AwaitingPickupArrival. Last observed: (nothing)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261014T132518155Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `c4f6235368774eba2c8a918b794c52f0942087de` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261014T132518155Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| V 在待送点扫码前取消：旅程 Completed / CANCELLED_BY_OPERATOR，V 停在 305，305 的单占仍是 V 的 | PASS | `CANCELLED_BY_OPERATOR / at 305 / 305 held by BROKERX-L2-0001` | `CANCELLED_BY_OPERATOR / at 305 / 305 held by BROKERX-L2-0001 FIXED_TASK_STATION` |
| V 离开等待点后那个点被放掉、空着：V 停在 305 时有一个开得过去的等待点 | PASS | `214 free` | `214 free=True` |
| V 在 305 停了十几轮：没有 IDLE_RETURN 用途占有、没有新的开往等待点的单，车仍在 305，305 仍是 V 的 | FAIL | `no claim / 1 intents / at 305 / held by BROKERX-L2-0001` | `claim idle-return:BROKERX-L2-0001:20261014T132545767Z / 2 intents / at 305 / held by BROKERX-L2-0001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
