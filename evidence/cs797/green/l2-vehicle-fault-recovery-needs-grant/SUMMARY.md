# L2 场景证据：vehicle-fault-recovery-needs-grant

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T150041733Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `b15e0703cbac87b6f33d5cc6d32ecc9f07d643f4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T150041733Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 订单 FAILED：故障记为 SuspectedBlocked（后面每一步拿这个故障当对象） | PASS | `SuspectedBlocked` | `SuspectedBlocked` |
| 没有授权：凭据对、条件齐全的 CLEAR_FAULT 回 403，理由只有 RECOVERY_GRANT_MISSING；又评估 3 轮之后故障仍是 SuspectedBlocked | PASS | `403 / RECOVERY_GRANT_MISSING / SuspectedBlocked` | `403 / RECOVERY_GRANT_MISSING / SuspectedBlocked` |
| 演练：200 PLANNED，给出 12 位确认码，计划只放 CLEAR_FAULT、5 分钟；服务端一条授权也没有，审计不落行 | PASS | `200 / PLANNED / 12 位码 / CLEAR_FAULT 5 分钟 / 0 条授权 / 0 行审计` | `200 / PLANNED / '035DD3435F4F' / CLEAR_FAULT 5 分钟 / 0 条授权 / 0 行审计` |
| 带码下发：200 GRANTED，只对车（不限旅程）、只放 CLEAR_FAULT；列表里正是这辆车；审计一行 RECOVERY_GRANT Succeeded | PASS | `200 / GRANTED / 不限旅程 / CLEAR_FAULT / 列表 AGV-L2-001 / RECOVERY_GRANT Succeeded` | `200 / GRANTED / 旅程 '' / CLEAR_FAULT / 列表 AGV-L2-001 / RECOVERY_GRANT Succeeded` |
| 授权只放 CLEAR_FAULT：RESUME_HELD_ORDER 回 403，理由 RECOVERY_GRANT_ACTION_NOT_GRANTED；故障不动 | PASS | `403 / RECOVERY_GRANT_ACTION_NOT_GRANTED / SuspectedBlocked` | `403 / RECOVERY_GRANT_ACTION_NOT_GRANTED / SuspectedBlocked` |
| 点名的 CLEAR_FAULT：200 Cleared，故障 Level None，ClearedReason 记着 L2-OPERATOR-797 | PASS | `200 / Cleared / None / L2-OPERATOR-797` | `200 / Cleared / None / FAULT_CLEARED_BY_OPERATOR:L2-OPERATOR-797` |
| 收回：200 REVOKED，列表为空；同一个 CLEAR_FAULT 再来一次是 403 RECOVERY_GRANT_MISSING，不是 AlreadyCleared（授权在判定之前查） | PASS | `200 / REVOKED / 0 条 / 403 RECOVERY_GRANT_MISSING` | `200 / REVOKED / 0 条 / 403 RECOVERY_GRANT_MISSING` |
| 1 分钟的授权：有效期内 CLEAR_FAULT 过得了授权这一关（200 AlreadyCleared）；到点后服务端自己列表变空，再来一次 403 RECOVERY_GRANT_MISSING，且是在 expiresAt 之后 | PASS | `200 / 200 AlreadyCleared / 0 条 / 403 RECOVERY_GRANT_MISSING / 已过 expiresAt` | `200 / 200 AlreadyCleared / 0 条 / 403 RECOVERY_GRANT_MISSING / expiresAt 10/21/2026 23:02:10 +08:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
