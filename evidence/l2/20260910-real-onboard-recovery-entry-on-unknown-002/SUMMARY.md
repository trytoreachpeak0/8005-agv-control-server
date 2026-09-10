# L2 场景证据：real-onboard-recovery-entry-on-unknown

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T015317170Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `2b1ece8a078b8efe73323d6d0f1dc7dd7ad2eb2d` |
| onboardHmiCommit | `3d8206fc9fc4ce02b58e78a205d9563be68c6b0e` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T015317170Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 真车载端报回不完美的装载结果，旅程停摆在 LOAD_RESULT_REQUIRES_RECOVERY | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 装载操作判 RecoveryRequired | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 失败结果已落库，且是这个 attempt 唯一一份存活结果 | PASS | `1 份，未 COMPLETED，未被替换` | `1 份，UNKNOWN` |
| 这一份 UNKNOWN 出自锁反馈失效，不是「人没放料」的另一种说法 | PASS | `UNKNOWN / UNKNOWN / ACTION_NOT_ALLOWED_IN_STATE` | `UNKNOWN / UNKNOWN / ACTION_NOT_ALLOWED_IN_STATE` |
| 车辆报的锁态与现场相反：它说已锁，门其实开着——这就是要人去看的理由 | PASS | `车报 LOCKED / 现场 OPEN / EMPTY` | `车报 LOCKED / 现场 OPEN / EMPTY` |
| 停摆之后车载端 HMI 上出现可用的恢复入口（任何一种恢复动作都行） | PASS | `True` | `True` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
