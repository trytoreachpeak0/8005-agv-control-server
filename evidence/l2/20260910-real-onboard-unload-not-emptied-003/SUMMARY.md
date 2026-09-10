# L2 场景证据：real-onboard-unload-not-emptied

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T121426982Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `56a06efae1622c0ba1b7ee491aca64b0d4e193ad` |
| onboardHmiCommit | `3cf26651111220cec67a370ac5b9f81bc21bd697` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T121426982Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装货一次到位，卸货那半边是从一个干净的起点开始的 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 关卡卸的是装货时用的那个仓位 | PASS | `1` | `1` |
| 第 1 轮：门关了货还在，仓位读数是明确的相反态而不是 UNKNOWN | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 第 1 轮：车载端自动重新开锁并再次提示，继续闭环而不是判失败 | PASS | `UNLOCKING > 1` | `2` |
| 第 1 轮：重开脉冲真的走到了 IO——门又弹开、开锁输出复位、车载端重新等操作员 | PASS | `OPEN / WAITING_OPERATOR > 1` | `OPEN / 2` |
| 第 2 轮：门关了货还在，仓位读数是明确的相反态而不是 UNKNOWN | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 第 2 轮：车载端自动重新开锁并再次提示，继续闭环而不是判失败 | PASS | `UNLOCKING > 2` | `3` |
| 第 2 轮：重开脉冲真的走到了 IO——门又弹开、开锁输出复位、车载端重新等操作员 | PASS | `OPEN / WAITING_OPERATOR > 2` | `OPEN / 3` |
| 重开 2 轮之后卸货操作仍在进行：既没有确定失败，也没有进恢复 | PASS | `Prepared` | `Prepared` |
| 一份卸货 OperationResult 都没发过——UnloadCompletionRequired 没有中途结算这回事 | PASS | `0` | `0` |
| 旅程原地等在卸货结果上，没有被任何取消分支带走 | PASS | `AwaitingUnloadResult` | `AwaitingUnloadResult` |
| 需求没有被取消也没有被判 RecoveryRequired | PASS | `Accepted` | `Accepted` |
| 会话全程停在 Ready：开着的仓门由本服务端自己下的命令解释 | PASS | `Ready` | `Ready / READY` |
| HMI 上没有出现恢复入口 | PASS | `False` | `False` |
| 唯一的终结方式是取空：取走之后仓位回到空、门关、锁上 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| journey 走到 Completed | PASS | `Completed` | `Completed` |
| 全程只有一个卸货 attempt，最终 Committed——重开不新建操作 | PASS | `Committed / 102b2f11-df0b-2b58-a0fc-9dd58a26394b` | `Committed / 102b2f11-df0b-2b58-a0fc-9dd58a26394b` |
| 需求终态为 Succeeded | PASS | `Succeeded` | `Succeeded` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 51 lines, 51 distinct, 11 message types, 0 distinct violations, 0 known; schema compilation 16489 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
