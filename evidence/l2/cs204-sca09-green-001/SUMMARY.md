# L2 场景证据：slot-configuration-activation-replay

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T131456565Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `7c1e5c718dbec17f7547c4db1e00303294448094` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260920T131456565Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 已批准的八仓事实入库，这台车八个仓位的 IO 都绑上了 | PASS | `OK / 8` | `OK / 8` |
| 下发回 202，激活处在待补报态，恢复角色是 SLOT_CONFIGURATION | PASS | `202 / PENDING_RESULT / SLOT_CONFIGURATION` | `202 / PENDING_RESULT / SLOT_CONFIGURATION` |
| 车收到的就是服务端落库的那条命令 | PASS | `e0d3a4f7-a3b7-4fa6-9665-c08178a0cfac` | `e0d3a4f7-a3b7-4fa6-9665-c08178a0cfac` |
| 断线期间服务端不猜：激活仍待补报，生效配置一行都没写 | PASS | `PENDING_RESULT / 0` | `PENDING_RESULT / 0` |
| 车上已经有结论，但一份结果都没送出去 | PASS | `1 outcome / 0 sent` | `1 outcome / 0 sent` |
| 重连走完完整握手，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 补报到达之后激活收敛为 ACTIVATED | PASS | `ACTIVATED` | `ACTIVATED` |
| 重连后补发的是同一条命令（同一个 messageId），不是新的一次下发 | PASS | `>=2 receipts of e0d3a4f7-a3b7-4fa6-9665-c08178a0cfac` | `e0d3a4f7-a3b7-4fa6-9665-c08178a0cfac,e0d3a4f7-a3b7-4fa6-9665-c08178a0cfac` |
| 车只报了一次结果（等到它记下发过一次、再多转三轮之后仍然是一次） | PASS | `1` | `1` |
| 一次下发只有一行激活，补报没有产生第二次激活 | PASS | `1` | `1` |
| 生效配置写了一行，就是这次激活、就是它的目标指纹 | PASS | `1 / 039a12e7-a0d5-4192-a9dd-6e65231a8321 / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` | `1 / 039a12e7-a0d5-4192-a9dd-6e65231a8321 / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` |
| 车重连后报的正是生效那一版，会话就绪，没有被判成指纹不符 | PASS | `Ready / READY` | `Ready / READY` |
| 业务审计经 FieldOps export-audit 导出为带 BOM 的 CSV，含这次激活的下发与结果两条 | PASS | `OK / BOM / ISSUED + RESULT_RECORDED` | `OK / BOM=True / count=7` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
