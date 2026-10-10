# L2 场景证据：slot-configuration-activation-replay

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163741324Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-3` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163741324Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 已批准的八仓事实入库，这台车八个仓位的 IO 都绑上了 | PASS | `OK / 8` | `OK / 8` |
| 下发回 202，激活处在待补报态，恢复角色是 SLOT_CONFIGURATION | PASS | `202 / PENDING_RESULT / SLOT_CONFIGURATION` | `202 / PENDING_RESULT / SLOT_CONFIGURATION` |
| 车收到的就是服务端落库的那条命令 | PASS | `009e685d-cc5c-430a-8732-b9d4ca73772b` | `009e685d-cc5c-430a-8732-b9d4ca73772b` |
| 断线期间服务端不猜：激活仍待补报，生效配置一行都没写 | PASS | `PENDING_RESULT / 0` | `PENDING_RESULT / 0` |
| 车上已经有结论，但一份结果都没送出去 | PASS | `1 outcome / 0 sent` | `1 outcome / 0 sent` |
| 重连走完完整握手，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 补报到达之后激活收敛为 ACTIVATED | PASS | `ACTIVATED` | `ACTIVATED` |
| 重连后补发的是同一条命令（同一个 messageId），不是新的一次下发 | PASS | `>=2 receipts of 009e685d-cc5c-430a-8732-b9d4ca73772b` | `009e685d-cc5c-430a-8732-b9d4ca73772b,009e685d-cc5c-430a-8732-b9d4ca73772b` |
| 车只报了一次结果（等到它记下发过一次、再多转三轮之后仍然是一次） | PASS | `1` | `1` |
| 一次下发只有一行激活，补报没有产生第二次激活 | PASS | `1` | `1` |
| 生效配置写了一行，就是这次激活、就是它的目标指纹 | PASS | `1 / 9bd38033-8fc1-4e91-a071-8cac5adc70e3 / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` | `1 / 9bd38033-8fc1-4e91-a071-8cac5adc70e3 / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` |
| 车重连后报的正是生效那一版，会话就绪，没有被判成指纹不符 | PASS | `Ready / READY` | `Ready / READY` |
| 业务审计经 FieldOps export-audit 导出为带 BOM 的 CSV，含这次激活的下发与结果两条 | PASS | `OK / BOM / ISSUED + RESULT_RECORDED` | `OK / BOM=True / count=10` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
