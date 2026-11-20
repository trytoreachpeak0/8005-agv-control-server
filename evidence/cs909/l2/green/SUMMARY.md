# L2 场景证据：multi-demand-stop-cancel-one-refreshes-at-once

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261120T062155581Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3983fb6e3eeeb8a86025239020fc86a8e37d5dec` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261120T062155581Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：乙、丁进了甲那一趟旅程，并进同一个 11 号站停靠；到站那一版清单列着丁、乙 | PASS | `B and D on one stop, arrival worklist lists both` | `B journey:2073e611-d7fa-4158-b30c-d740376bda03\|PICKUP, D journey:2073e611-d7fa-4158-b30c-d740376bda03\|PICKUP; arrival 3[2]@2026-11-20T06:23:47.4939090+00:00` |
| 乙终结；只列丁的那一版号更高，写进发件箱的时刻就是乙的抑制时刻（与取消同一次保存） | PASS | `worklist [D] above 3 created at 2026-11-20T06:23:47.7159730+00:00` | `B TERMINATED; suppressed 2026-11-20T06:23:47.7159730+00:00; worklists 3[2]@2026-11-20T06:23:47.4939090+00:00 4[1]@2026-11-20T06:23:47.7159730+00:00` |
| 没有一条还会补发的 11 号站录入请求期待乙；同号的录入请求只期待丁 | PASS | `no pending request for L2-CH-B-20261120T062155581Z; [L2-CH-D-20261120T062155581Z] at the new revision` | `pending 4:L2-CH-D-20261120T062155581Z (expecting B: 0, D alone at the new revision: 1)` |
| 合成车载端收到新号的录入请求，答它，丁装上 | PASS | `entry sublot:d5f5edfb-6e50-dd51-8817-c9fbc52be399:4 held and answered, D LOADED` | `held yes, D LOADED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
