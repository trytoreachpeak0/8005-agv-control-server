# L2 场景证据：g3-load-cancellation-before-load

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T151423404Z` |
| agvId | `AGV-L2-001` |
| batchId | `fti-v5` |
| controlServerCommit | `7b302c9ceeac2eac8a44545d8b76304819e0f4b5` |
| onboardHmiCommit | `adc1a28f153dc17aa54dcff1400abcc4bc21ff0b` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T151423404Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到站、录入请求挂着、还没录入任何子批时，车载端清单里这一行出现可用的「取消」（机台取货停靠每行一个，CP-0016；不依赖恢复开关） | FAIL | `row 取消 pressable=True` | `row 取消 pressable=False / bottom 取消装货 offered=True` |
| 未到达：车载端清单里这一行没有可按的「取消」 | FAIL | `(reached)` | `(not reached) 车载端清单里这一行没有可按的「取消」` |
| 未到达：车载端清单里这一行没有可按的「取消」 | FAIL | `(reached)` | `(not reached) 车载端清单里这一行没有可按的「取消」` |
| 未到达：车载端清单里这一行没有可按的「取消」 | FAIL | `(reached)` | `(not reached) 车载端清单里这一行没有可按的「取消」` |
| 未到达：车载端清单里这一行没有可按的「取消」 | FAIL | `(reached)` | `(not reached) 车载端清单里这一行没有可按的「取消」` |
| 未到达：车载端清单里这一行没有可按的「取消」 | FAIL | `(reached)` | `(not reached) 车载端清单里这一行没有可按的「取消」` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
