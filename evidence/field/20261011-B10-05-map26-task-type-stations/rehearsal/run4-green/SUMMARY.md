# L2 场景证据：b10-05-map26-activation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261011T010005132Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `da6e40e27b2dd4ef00a1d9f920281e1c3191aca5` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261011T010005132Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端完整确认的 26 号图目录就是 catalog-26.json（216 个站，修订 3498438256227867560） | PASS | `3498438256227867560` | `3498438256227867560` |
| 预置装进来的是版本 1：只有 WIRE_TO_GATE → 210，旧身份核对号 | PASS | `1: 210/关卡/MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919` | `1: 210/关卡/MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919` |
| 复现 factory01：激活 217 + 210 得到版本 2 | PASS | `OK, version 2` | `OK, version 2` |
| dry-run 预览通过、无违规，变化恰好是四类新增（210、217 只换记录号，预览不列），什么都没切换 | PASS | `PREVIEW, 0 violations, added DIE_TO_OVEN,DIE_TO_WIRE_STAGING,WIRE_TO_NITROGEN,WIRE_TO_OPTICAL` | `PREVIEW, 0 violations, DIE_TO_OVEN,DIE_TO_WIRE_STAGING,WIRE_TO_NITROGEN,WIRE_TO_OPTICAL` |
| dry-run 之后生效版本仍是 2 | PASS | `2` | `2` |
| 正式激活得到版本 3 | PASS | `OK, version 3` | `OK, version 3` |
| 只读核对：生效版本 3、六条绑定与候选逐条相同、需求集六类、没有暂停、没有未结尝试 | PASS | `3: 210/关卡/MAP-26-WIRE_TO_GATE-SITE-20261010; 217/派工待送取货/MAP-26-STAGING_TO_WIRE-SITE-20261010; 218/烘箱/MAP-26-DIE_TO_OVEN-SITE-20261010; 219/三光/MAP-26-WIRE_TO_OPTICAL-SITE-20261010; 220/装片氮气柜/MAP-26-DIE_TO_WIRE_STAGING-SITE-20261010; 221/焊线氮气柜/MAP-26-WIRE_TO_NITROGEN-SITE-20261010` | `3: 210/关卡/MAP-26-WIRE_TO_GATE-SITE-20261010; 217/派工待送取货/MAP-26-STAGING_TO_WIRE-SITE-20261010; 218/烘箱/MAP-26-DIE_TO_OVEN-SITE-20261010; 219/三光/MAP-26-WIRE_TO_OPTICAL-SITE-20261010; 220/装片氮气柜/MAP-26-DIE_TO_WIRE_STAGING-SITE-20261010; 221/焊线氮气柜/MAP-26-WIRE_TO_NITROGEN-SITE-20261010` |
| 回滚到版本 2 生效为新版本 4，内容与版本 2 相同 | PASS | `4 = content of 2` | `4: 217/派工待送取货/MAP-26-STAGING_TO_WIRE-SITE-20261009; 210/关卡/MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919` |
| bad-1-area-named-station.json 被拒，退出码 1，原因码恰好是 TASK_TYPE_BINDING_AREA_NAMED_STATION | PASS | `1 REJECTED TASK_TYPE_BINDING_AREA_NAMED_STATION` | `1 REJECTED TASK_TYPE_BINDING_AREA_NAMED_STATION` |
| bad-2-station-reused.json 被拒，退出码 1，原因码恰好是 TASK_TYPE_STATION_REUSED | PASS | `1 REJECTED TASK_TYPE_STATION_REUSED` | `1 REJECTED TASK_TYPE_STATION_REUSED` |
| bad-3-site-verification-missing.json 被拒，退出码 1，原因码恰好是 TASK_TYPE_BINDING_SITE_VERIFICATION_MISSING | PASS | `1 REJECTED TASK_TYPE_BINDING_SITE_VERIFICATION_MISSING` | `1 REJECTED TASK_TYPE_BINDING_SITE_VERIFICATION_MISSING` |
| 三份错误候选之后生效版本仍是 4，没有版本 5 | PASS | `4 of 4` | `4 of 4` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
