# L2 场景证据：demand-bearing-store-at-unload

结论：**FAIL**

失败原因：Build failed; see C:\Users\szy\Desktop\8005-workspace-v2\worktrees\b8-541-8005-agv-control-server\evidence\g3\cs541-runner\demand-bearing-selfcheck-m3-63fe7c61\store-generator\logs\build.log

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T005033079Z` |
| agvId | `(null)` |
| batchId | `unspecified` |
| controlServerCommit | `63fe7c61e35796bb303e97300e2ccad7285c085b` |
| protocolReleaseIdentity | `(null)` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T005033079Z` |
| vehicleKey | `(null)` |

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
