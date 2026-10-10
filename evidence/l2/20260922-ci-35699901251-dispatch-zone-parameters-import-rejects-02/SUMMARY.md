# L2 场景证据：dispatch-zone-parameters-import-rejects

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T075115030Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075115030Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端以目标配置起过一次，调度策略里有分区 MAP-25-WIRE_TO_GATE；导入判「分区存在」就按这张表 | PASS | `>= 1` | `1` |
| 默认前置是「每区参数未配置」：导入之前一版都没有，也没有快照与导入审计 | PASS | `0/0/0` | `0/0/0` |
| 表头把途中追加一列写成了秒——单位写在列名里，单位错的表在表头就被拦下：退出码 1、整份拒绝、原因码与行号精确，库里版本、快照、导入审计都没多 | PASS | `1 / REJECTED / 1 DISPATCH_ZONE_PARAMETERS_CSV_HEADER_INVALID / 0/0/0` | `1 / REJECTED / 1 DISPATCH_ZONE_PARAMETERS_CSV_HEADER_INVALID / 0/0/0` |
| 一行少了一个字段：退出码 1、整份拒绝、原因码与行号精确，库里版本、快照、导入审计都没多 | PASS | `1 / REJECTED / 2 DISPATCH_ZONE_PARAMETERS_CSV_ROW_MALFORMED / 0/0/0` | `1 / REJECTED / 2 DISPATCH_ZONE_PARAMETERS_CSV_ROW_MALFORMED / 0/0/0` |
| 同一个分区出现两次，两行的途中追加上限还不一样：退出码 1、整份拒绝、原因码与行号精确，库里版本、快照、导入审计都没多 | PASS | `1 / REJECTED / 3 DISPATCH_ZONE_DUPLICATED / 0/0/0` | `1 / REJECTED / 3 DISPATCH_ZONE_DUPLICATED / 0/0/0` |
| 分区不在库内当前调度策略里：退出码 1、整份拒绝、原因码与行号精确，库里版本、快照、导入审计都没多 | PASS | `1 / REJECTED / 3 DISPATCH_ZONE_NOT_FOUND / 0/0/0` | `1 / REJECTED / 3 DISPATCH_ZONE_NOT_FOUND / 0/0/0` |
| 途中追加上限写成负数：退出码 1、整份拒绝、原因码与行号精确，库里版本、快照、导入审计都没多 | PASS | `1 / REJECTED / 2 DISPATCH_ZONE_PARAMETER_VALUE_INVALID / 0/0/0` | `1 / REJECTED / 2 DISPATCH_ZONE_PARAMETER_VALUE_INVALID / 0/0/0` |
| 正确的表被收下：退出码 0，恰好形成第 1 版，快照一条、业务审计一条 | PASS | `0 / OK / v1 / 1/1/1` | `0 / OK / v1 / 1/1/1` |
| 只读动词读回的就是导入的那一版：途中追加上限 20000 毫米、防饥饿阈值 600 秒，内容哈希与导入时一致 | PASS | `OK / v1 / 4dd3eb83b075a8fedf0aae083c88f18e2f1386653f468f0d0e9b17b35587067a / ALLOWED/20000 CONFIGURED/600` | `OK / v1 / 4dd3eb83b075a8fedf0aae083c88f18e2f1386653f468f0d0e9b17b35587067a / ALLOWED/20000 CONFIGURED/600` |
| 不停车生效：导入前后是同一个服务端进程（PID 与启动时刻都相同），导入之后它仍然就绪 | PASS | `pid 11672 ControlServer.Host started 2026-09-22T15:51:20.1756662+08:00 / 200` | `pid 11672 ControlServer.Host started 2026-09-22T15:51:20.1756662+08:00 / 200` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
