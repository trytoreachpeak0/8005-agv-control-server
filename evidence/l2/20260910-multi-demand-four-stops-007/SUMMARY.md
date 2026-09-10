# L2 场景证据：multi-demand-four-stops

结论：**FAIL**

失败原因：Build failed; see C:\Users\szy\Desktop\8005-workspace\repos\8005-agv-control-server\evidence\l2\20260910-multi-demand-four-stops-007\logs\build.log

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T075621505Z` |
| agvId | `` |
| batchId | `BATCH-3` |
| controlServerCommit | `bc63153a7b746816904bc9e744b426901a70f712` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T075621505Z` |
| vehicleKey | `` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
