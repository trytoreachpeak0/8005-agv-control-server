# L2 场景证据：slot-configuration-activation-replay

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260912T112009758Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-3` |
| controlServerCommit | `17cf478323f9c97e4aaea3f7c7a3cef732908249` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `16e2567a7033883f00fc999f7fa08f954dd13a26` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0` |
| protocolReleaseIdentity.schemaBundleSha256 | `225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Temp\l2-20260912T112009758Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 已批准的八仓事实入库，这台车八个仓位的 IO 都绑上了 | PASS | `OK / 8` | `OK / 8` |
| 下发回 202，激活处在待补报态，恢复角色是 SLOT_CONFIGURATION | PASS | `202 / PENDING_RESULT / SLOT_CONFIGURATION` | `202 / PENDING_RESULT / SLOT_CONFIGURATION` |
| 车收到的就是服务端落库的那条命令 | PASS | `a544bf9d-79e8-4097-aecd-e87591f3af18` | `a544bf9d-79e8-4097-aecd-e87591f3af18` |
| 断线期间服务端不猜：激活仍待补报，生效配置一行都没写 | PASS | `PENDING_RESULT / 0` | `PENDING_RESULT / 0` |
| 车上已经有结论，但一份结果都没送出去 | PASS | `1 outcome / 0 sent` | `1 outcome / 0 sent` |
| 重连走完完整握手，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 补报到达之后激活收敛为 ACTIVATED | PASS | `ACTIVATED` | `ACTIVATED` |
| 重连后补发的是同一条命令（同一个 messageId），不是新的一次下发 | PASS | `>=2 receipts of a544bf9d-79e8-4097-aecd-e87591f3af18` | `a544bf9d-79e8-4097-aecd-e87591f3af18,a544bf9d-79e8-4097-aecd-e87591f3af18` |
| 车只报了一次结果 | PASS | `1` | `1` |
| 一次下发只有一行激活，补报没有产生第二次激活 | PASS | `1` | `1` |
| 生效配置写了一行，就是这次激活、就是它的目标指纹 | PASS | `1 / b2066154-0b1c-4c3f-87ee-6178cd4d6ced / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` | `1 / b2066154-0b1c-4c3f-87ee-6178cd4d6ced / de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` |
| 车重连后报的正是生效那一版，会话就绪，没有被判成指纹不符 | PASS | `Ready / READY` | `Ready / READY` |
| 业务审计经 FieldOps export-audit 导出为带 BOM 的 CSV，含这次激活的下发与结果两条 | PASS | `OK / BOM / ISSUED + RESULT_RECORDED` | `OK / BOM=True / count=5` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
