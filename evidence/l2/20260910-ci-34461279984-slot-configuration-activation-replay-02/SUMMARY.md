# L2 场景证据：slot-configuration-activation-replay

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T094734901Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-3` |
| controlServerCommit | `b38b9ab710b237605832c448a01df698c5c88c08` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788` |
| protocolReleaseIdentity.schemaBundleSha256 | `71146c881e8ec199e9a977779ec1a557bed96a9ab71e36cfc3dfb7b329351c6b` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Temp\l2-20260910T094734901Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 已批准的八仓事实入库，这台车八个仓位的 IO 都绑上了 | PASS | `OK / 8` | `OK / 8` |
| 下发回 202，激活处在待补报态，恢复角色是 SLOT_CONFIGURATION | PASS | `202 / PENDING_RESULT / SLOT_CONFIGURATION` | `202 / PENDING_RESULT / SLOT_CONFIGURATION` |
| 车收到的就是服务端落库的那条命令 | PASS | `711cd34c-15da-4a40-90bc-2137240ce35c` | `711cd34c-15da-4a40-90bc-2137240ce35c` |
| 断线期间服务端不猜：激活仍待补报，生效配置一行都没写 | PASS | `PENDING_RESULT / 0` | `PENDING_RESULT / 0` |
| 车上已经有结论，但一份结果都没送出去 | PASS | `1 outcome / 0 sent` | `1 outcome / 0 sent` |
| 重连走完完整握手，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 补报到达之后激活收敛为 ACTIVATED | PASS | `ACTIVATED` | `ACTIVATED` |
| 重连后补发的是同一条命令（同一个 messageId），不是新的一次下发 | PASS | `>=2 receipts of 711cd34c-15da-4a40-90bc-2137240ce35c` | `711cd34c-15da-4a40-90bc-2137240ce35c,711cd34c-15da-4a40-90bc-2137240ce35c` |
| 车只报了一次结果 | PASS | `1` | `1` |
| 一次下发只有一行激活，补报没有产生第二次激活 | PASS | `1` | `1` |
| 生效配置写了一行，就是这次激活、就是它的目标指纹 | PASS | `1 / 05b02ecd-3712-44ae-923a-20139dc92c98 / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` | `1 / 05b02ecd-3712-44ae-923a-20139dc92c98 / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` |
| 车重连后报的正是生效那一版，会话就绪，没有被判成指纹不符 | PASS | `Ready / READY` | `Ready / READY` |
| 业务审计经 FieldOps export-audit 导出为带 BOM 的 CSV，含这次激活的下发与结果两条 | PASS | `OK / BOM / ISSUED + RESULT_RECORDED` | `OK / BOM=True / count=5` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
