# L2 场景证据：real-onboard-resume-after-repair

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260903T162413310Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `c9bbf02040008527de2734605561d89bf136f0a8` |
| onboardHmiCommit | `f0465d9ad9f84607e3db972f1c6cb0ead910ab3d` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260903T162413310Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 真车载端报回不完美的装载结果，旅程停摆在 LOAD_RESULT_REQUIRES_RECOVERY | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 装载操作判 RecoveryRequired | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 失败结果已落库，且是这个 attempt 唯一一份存活结果 | PASS | `1 份，未 COMPLETED，未被替换` | `1 份，UNKNOWN` |
| 仓位物理事实与失败一致：门关着、锁上了，但仍然是空的 | PASS | `CLOSED/EMPTY` | `CLOSED/EMPTY` |
| 停摆之后车载端 HMI 上出现可用的「申请恢复」入口 | FAIL | `True` | `False` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
