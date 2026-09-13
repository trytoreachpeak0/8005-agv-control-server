# L2 场景证据：real-onboard-field-continuous-mixed-rehearsal

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T085235295Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `fb0a8898e5fcdb70cc8e0698d690a1f0665d98f8` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260913T085235295Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 开跑时会话 Ready、没有未完成旅程 | PASS | `(无未完成旅程)` | `(无未完成旅程)` |
| 一次连续驱动开完两趟，两趟都 Completed、都不需要续跑 | PASS | `2 趟 / Completed×1` | `2 趟 / Completed×1,Completed×1` |
| 第一趟四站按选择分派：T 被站点期限终结、X 被操作员取消，各带抑制原因；后两站照常装货提交 | PASS | `1:T:Cancelled(CANCELLED_BY_STATION_TIMEOUT),2:X:Cancelled(CANCELLED_BY_OPERATOR),3:N:Committed,4:N:Committed` | `1:T:Cancelled(CANCELLED_BY_STATION_TIMEOUT),2:X:Cancelled(CANCELLED_BY_OPERATOR),3:N:Committed,4:N:Committed` |
| 第一趟关卡：只卸真正装上的两条，都 Committed | PASS | `2 条 Committed` | `Committed,Committed` |
| 第二趟整趟被终结（取消成功，或按钮没出现、交给站点期限）：旅程 Completed、没有卸货动作、关卡停靠没有被走到 | PASS | `1:X:Cancelled(CANCELLED_BY_OPERATOR) 或 1:X-UNOFFERED:Cancelled(CANCELLED_BY_STATION_TIMEOUT) / 0 卸货 / 关卡未走` | `1:X-UNOFFERED:Cancelled(CANCELLED_BY_STATION_TIMEOUT) / 0 卸货 / 关卡 PLANNED` |
| 选择回调每个取货站只被问一次 | PASS | `1:1:T 1:2:X 1:3:N 1:4:N 2:1:X` | `1:1:T 1:2:X 1:3:N 1:4:N 2:1:X` |
| 全程没有旅程 Blocked，会话一直是开跑时那一代、仍 Ready | PASS | `0 / generation 1 / Ready` | `0 / generation 1 / Ready` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 110 lines, 110 distinct, 12 message types, 0 distinct violations, 0 known; schema compilation 24207 ms.` |

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
