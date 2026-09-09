# L2 场景证据：real-onboard-load-door-closed-empty

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260909T094432780Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `0f6b42458ab55cde4c9123ad344ddc86c3fe5774` |
| onboardHmiCommit | `98df671c846c7c572fd40d38fe16e5e084686603` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260909T094432780Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端说在等操作员时，它要开的那个仓门确实开着、货位是空的 | PASS | `OPEN/EMPTY/0/0` | `OPEN/EMPTY/0/0` |
| 第 1 轮：操作员关门但没放料，仓位读数是明确的相反态而不是 UNKNOWN | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 第 1 轮：车载端自动重新开锁并再次提示，没有把「人没放料」判成失败 | PASS | `UNLOCKING > 1` | `2` |
| 第 1 轮：重开脉冲真的走到了 IO——门又弹开、开锁输出复位、车载端重新等操作员 | PASS | `OPEN / WAITING_OPERATOR > 1` | `OPEN / 2` |
| 第 2 轮：操作员关门但没放料，仓位读数是明确的相反态而不是 UNKNOWN | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 第 2 轮：车载端自动重新开锁并再次提示，没有把「人没放料」判成失败 | PASS | `UNLOCKING > 2` | `3` |
| 第 2 轮：重开脉冲真的走到了 IO——门又弹开、开锁输出复位、车载端重新等操作员 | PASS | `OPEN / WAITING_OPERATOR > 2` | `OPEN / 3` |
| 重开 2 轮之后装载操作仍在进行，没有进人工恢复 | PASS | `Prepared` | `Prepared` |
| 一份 OperationResult 都没发过——操作还没结束，无所谓成败 | PASS | `0` | `0` |
| 会话全程停在 Ready：开着的仓门由本服务端自己下的命令解释，不是会话故障 | PASS | `Ready` | `Ready / READY` |
| HMI 上没有出现恢复入口——操作员迟疑不需要管理员凭据 | PASS | `False` | `False` |
| 门一直开着时提示节拍到期又提示了一次，操作没有因此结束 | PASS | `WAITING_OPERATOR > 3` | `4` |
| 那一次提示没有跟着一次重复脉冲：门本来就开着，再开一次没有意义 | PASS | `3` | `3` |
| 等满一个 OperationTimeout 之后仍然没有结果、没有恢复——它不再是判死依据 | PASS | `Prepared / 0 result` | `Prepared / 0 result` |
| 最后一轮走通真 Modbus 闭环：放料、关门、锁反馈回到 1、开锁输出复位 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 前两幕没有把这次装载弄坏，它照常提交并进入去关卡那一段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| 全程只有一个 attempt，最终 Committed——重开不新建操作 | PASS | `Committed / 588efa35-f6dd-0e5e-880c-2790d7da3dc5` | `Committed / 588efa35-f6dd-0e5e-880c-2790d7da3dc5` |
| 整趟只发了一份 OperationResult，就是最后成功的那一份 | PASS | `1` | `1` |
| 需求从头到尾没有被判 RecoveryRequired | PASS | `Accepted` | `Accepted` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
