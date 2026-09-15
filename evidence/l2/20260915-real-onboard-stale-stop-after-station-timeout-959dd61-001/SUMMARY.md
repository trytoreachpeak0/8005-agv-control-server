# L2 场景证据：real-onboard-stale-stop-after-station-timeout

结论：**FAIL**

失败原因：Peer repository not found: C:\Users\szy\8005-wt\slots-simulator

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260915T074032069Z` |
| agvId | `` |
| batchId | `BATCH-3` |
| controlServerCommit | `f24663b54cfabfa0981a428929deda4faefbe8f3` |
| onboardHmiCommit | `959dd615df84496bd654641d87d9df929ce28698` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260915T074032069Z` |
| vehicleKey | `` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | FAIL | `退出码 0，校验行数 > 0，未登记违约 0` | `校验没有跑成：No protocol lines were recorded: C:\Users\szy\AppData\Local\Temp\l2-20260915T074032069Z\real-onboard-inbound.ndjson does not exist.` |

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
