# L2 场景证据：real-onboard-expected-action-overdue

结论：**FAIL**

失败原因：The reopen came 0 s after the first unlock; that is too close to tell 'counted from the first unlock' from 'counted from the reopen' within 00:00:02.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T070023926Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `a30ddd724e958ac7bd1ee90830812d48d3aac798` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `968d213707786dea63ccbdf12421aad42e9857e4` |
| protocolFaultProxy | `True` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\vmwin01-control-server-desktop\_work\_temp\real-rig-39558343471-1\_stage\l2-20261021T070023926Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 空关后车不自己重开（5 秒门关着、锁扣上、没有 UNLOCKING）；按「重新开门」后重开：这次装货的 UNLOCKING 进度 ≥ 2 条，1 号仓门又弹开、空着、开锁输出复位，重开早于门槛 | FAIL | `空关后车没有自己重开 / 按了重新开门、重开 / UNLOCKING ≥ 2 / OPEN/EMPTY/0/0 / 重开早于门槛` | `空关后车没有自己重开（UNLOCKING 1→1；+0s CLOSED/EMPTY/0/0 -> +0.2s CLOSED/EMPTY/1/0） / 第 1 次按键 30 秒没有重开：屏上 absent，UNLOCKING 1→1，OPEN/EMPTY/0/0 / UNLOCKING 1 / OPEN/EMPTY/0/0 / 重开于第一次开锁后 0 s` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
