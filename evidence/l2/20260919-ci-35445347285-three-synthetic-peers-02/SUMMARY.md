# L2 场景证据：three-synthetic-peers

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T134057201Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `50dc987d680a33c3f8732619a91f5761453ddd63` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T134057201Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 编排器按 setup 起了三个合成车载端 | PASS | `3` | `3` |
| 三个控制面各占一个端口 | PASS | `3` | `3` |
| 三个车载端各报各的 AgvId | PASS | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` |
| AGV-FAKE-001 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-001` | `AGV-FAKE-001` |
| AGV-FAKE-002 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-002` | `AGV-FAKE-002` |
| AGV-FAKE-003 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-003` | `AGV-FAKE-003` |
| 三个控制面报出三个互不相同的身份 | PASS | `3` | `3` |
| 服务端同时持有三台车的会话，一台一行 | PASS | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` |
| AGV-FAKE-001 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |
| AGV-FAKE-002 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |
| AGV-FAKE-003 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
