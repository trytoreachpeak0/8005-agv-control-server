# L2 场景证据：real-onboard-restart-while-waiting-operator

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T132533927Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `de29ec1649596012e39bb71de45f22ba4ff10605` |
| onboardHmiCommit | `24af41e4ab769f10cd15381fd3b4a56babb62c78` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35513390399-1\_stage\l2-20260920T132533927Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 起点到位：车载端开了锁、在等操作员，门开着，装货操作 Prepared，装货命令还没被结算 | PASS | `OPEN / Prepared / 命令未结算` | `OPEN / Prepared / 命令未结算` |
| 车载端退出时没有留下结果：服务端仍在转（运行时又转过 2 轮以上）而收件箱不再增长、行数不少于杀之前，此时 OperationResults 0 行，装货操作仍是 Prepared | PASS | `收件箱 22 行以上且稳定 2 s 以上 / 运行时 2 轮以上 / 0 行 / Prepared` | `收件箱 22 行（杀前 22），稳定 2.1 s，其间运行时转了 2 轮 / 0 行 / Prepared` |
| 重启后车载端握手如实上报那一次没了结的 attempt | PASS | `f50bd624-79e9-f855-a4fa-34b87f74be4d` | `f50bd624-79e9-f855-a4fa-34b87f74be4d` |
| 重启后车载端按实时 IO 补交结果：仓位全到最终态，报 COMPLETED，物理字段是重启后读到的 OCCUPIED / LOCKED / RESET，服务端 DurableAck 收下（ADR-cross-0058 决策 2） | PASS | `COMPLETED / COMPLETED / OCCUPIED / LOCKED / RESET → DurableAck` | `COMPLETED / COMPLETED / OCCUPIED / LOCKED / RESET → DurableAck` |
| 不进恢复：服务端在新世代判会话 Ready，装货 Committed，旅程直接往关卡走、从未停摆，没有恢复会话也没有恢复工作流 | PASS | `gen > 1 Ready / Committed / AwaitingGateArrival / 无停摆 / 恢复会话 0 / 工作流 0` | `gen 2 / Ready / READY / Committed / AwaitingGateArrival / 停摆 无 / 恢复会话 0 / 工作流 0` |
| 重启前挂着的装货命令被补交的结果结算掉 | PASS | `已结算` | `已结算` |
| 旅程照常走完：卸的是装货那一仓，需求 Succeeded，装货结果只记了一次 | PASS | `Completed / Succeeded / 仓 1 / 1` | `Completed / Succeeded / 仓 1 / 1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
