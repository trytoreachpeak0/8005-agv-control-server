# L2 场景证据：en-route-append-not-configured

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161810486Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161810486Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 本区一版派车参数都没有：这正是参数批准之前现场的样子 | PASS | `0` | `0` |
| 第一条需求被一辆空闲车接走，车已在途 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 第二条需求被判为本区未配置途中追加（EN_ROUTE_APPEND_NOT_CONFIGURED），不是被别的什么挡住 | PASS | `EN_ROUTE_APPEND_NOT_CONFIGURED` | `EN_ROUTE_APPEND_NOT_CONFIGURED` |
| 那趟旅程仍然只带着一条需求：追加一次都没有发生 | PASS | `1` | `1` |
| 停靠仍然是两个：计划一个字都没被改写 | PASS | `2` | `2` |
| 第二条需求没有被受理过：积压行上没有受理时刻，它在等下一辆空闲车 | PASS | `(无受理时刻)` | `(无受理时刻)` |
| 计划只发过一版：没有追加，就没有整体重发 | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
