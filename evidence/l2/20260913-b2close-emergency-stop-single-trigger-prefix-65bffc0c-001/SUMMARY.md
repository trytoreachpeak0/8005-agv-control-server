# L2 场景证据：emergency-stop-single-trigger

结论：**FAIL**

失败原因：Timed out after 30s waiting for: the trigger was settled as confirmed. Last observed: "Pending"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T084206345Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `65bffc0c60591648a11f7c389d5cbcd27ee103e1` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260913T084206345Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在正常行驶、没有任何故障时，一次急停都没发 | PASS | `0 行 / 0 次` | `0 行 / 0 次` |
| 该发时发了：单被报 FAILED 且车证不出停住，服务端发出 triggerEmergency，记在这台车名下 | PASS | `AGV-L2-001 / vehicle:BROKERX-L2-0001` | `AGV-L2-001 / vehicle:BROKERX-L2-0001` |
| 线上收到的是 triggerEmergency，打在这台车的 deviceKey 上 | PASS | `triggerEmergency @ BROKERX-L2-0001` | `1 次，首次打在 BROKERX-L2-0001` |
| 回读时闩锁还没锁上，这一行记为 Pending，没有被当成已停住 | PASS | `Pending` | `Pending` |
| 只发一次：闩锁还没锁上、车仍在动，又评估了 4 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | FAIL | `1 行 / 1 次` | `5 行 / 5 次` |
| 只发一次：闩锁读不到（不是 OK，也不是锁上），又评估了 4 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | FAIL | `1 行 / 1 次` | `9 行 / 9 次` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
