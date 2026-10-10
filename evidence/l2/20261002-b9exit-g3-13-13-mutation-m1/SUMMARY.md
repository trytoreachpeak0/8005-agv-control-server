# L2 场景证据：g3-manual-station-clearance

结论：**FAIL**

失败原因：Publishing onboard-hmi failed; see C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs412-8005-agv-control-server\evidence\l2\20261002-b9exit-g3-13-13-mutation-m1\logs\build-onboard-hmi.log

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T142135933Z` |
| agvId | `(null)` |
| batchId | `unspecified` |
| controlServerCommit | `b0f070db035c14165ea6459259a556047b9cf56d` |
| protocolReleaseIdentity | `(null)` |
| rig | `RealOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261002T142135933Z` |
| vehicleKey | `(null)` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
