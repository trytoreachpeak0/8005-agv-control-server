# L2 场景证据：real-onboard-mixed-side-one-stop

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T184430991Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `b43fa390ce616913f13af0c04b34e39e61b3d9b1` |
| onboardHmiCommit | `deeba94c42c51561621634194d3c9f6582737486` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T184430991Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：乙丙都进了甲那一趟旅程，取货并成 12 号站的同一个停靠（归属三条、停靠 11→12→关卡） | PASS | `归属 3 / 乙丙同一个停靠 @12 / 1:PICKUP@11 2:PICKUP@12 3:UNLOAD@210` | `归属 3 / 乙丙同一个停靠 @12 / 1:PICKUP@11 2:PICKUP@12 3:UNLOAD@210` |
| 服务端：12 号站被车载端确认的清单里有一版同时列着乙丙两条清单项（正事实，从发件箱读） | PASS | `L2-MSO-B-20260921T184430991Z,L2-MSO-C-20260921T184430991Z` | `worklist r2 @N1-3_N2-5 L2-MSO-C-20260921T184430991Z/PICKUP,L2-MSO-B-20260921T184430991Z/PICKUP` |
| 车载端：清单确认之后，UIA 的本站清单列表（WorklistItems）同时有乙丙两行 | PASS | `两行 L2-MSO-B-20260921T184430991Z,L2-MSO-C-20260921T184430991Z` | `L2-MSO-C-20260921T184430991Z/UNASSIGNED,L2-MSO-B-20260921T184430991Z/UNASSIGNED` |
| 装货先前后后、第二条命令在第一条闭环之后才开锁：丙（后侧）的装货命令建在乙（前侧）的装货提交之后；模拟器采样里丙那一仓第一次未锁闭晚于乙那一仓最后一次未锁闭（REQ-0357） | PASS | `丙命令 ≥ 乙提交 / 采样 乙仓最后 < 丙仓最先` | `乙提交 2026-09-22T02:46:16.3722496+08:00 丙命令 2026-09-21T18:46:17.4094451+00:00 / 采样 乙仓 2 [3..3] 丙仓 5 [5..5]` |
| 装货各开各组：乙的目标仓与开的仓在前侧组、丙的在后侧组、甲的在前侧组，开的仓就是目标仓（一条需求一仓） | PASS | `甲 FRONT / 乙 FRONT / 丙 REAR，开的仓 = 目标仓` | `甲 1→FRONT / 乙 2→FRONT / 丙 5→REAR` |
| 装完时模拟器每个仓的物理状态与三条需求的 TargetSlotsJson 一致：目标仓有货、其余全空，全部关门、锁上、开锁输出复位；每条需求的目标仓都在它区域指派的那一组（有货的是前侧两仓、后侧一仓） | PASS | `有货 [1,2,5]，其余空，全部 CLOSED/1/0 / 有货的组 FRONT,FRONT,REAR` | `目标 [1,2,5] / 不一致 0 仓 / 有货的组 FRONT,FRONT,REAR` |
| 持货等单确实发生过：12 号站装完后装货阶段是 CARGO_HOLDING_WAIT、发给车的快照里有它，车载端 UIA 读到持货倒计时 ACTIVE | PASS | `CARGO_HOLDING_WAIT / 快照有 WAIT / 倒计时 ACTIVE` | `AwaitingStationDeparture CARGO_HOLDING_WAIT / WAIT 快照 2 张 / 倒计时 ACTIVE` |
| 持货收尾原因属于 {CARGO_HOLDING_TIMEOUT, VEHICLE_FULL}，不是让站 WAITING_STATION_YIELD（车队只有这一辆车）；发给车的关闭快照是同一个原因 | PASS | `CLOSED/CARGO_HOLDING_TIMEOUT 或 CLOSED/VEHICLE_FULL，快照一致` | `AwaitingGateArrival CLOSED/CARGO_HOLDING_TIMEOUT / 快照原因 CARGO_HOLDING_TIMEOUT` |
| 车载端：关卡清单确认之后，UIA 清单列表上乙那一行的侧标 FRONT、丙那一行 REAR（onboard-hmi#134 WorklistItemSide） | PASS | `L2-MSO-B-20260921T184430991Z/FRONT, L2-MSO-C-20260921T184430991Z/REAR` | `worklist r4 @关卡 L2-MSO-C-20260921T184430991Z/DROPOFF,L2-MSO-B-20260921T184430991Z/DROPOFF,L2-MSO-A-20260921T184430991Z/DROPOFF / 界面 L2-MSO-C-20260921T184430991Z/REAR,L2-MSO-B-20260921T184430991Z/FRONT,L2-MSO-A-20260921T184430991Z/FRONT` |
| 卸货一次一扇、先前后后（规格第 20 节，不是第 8.3 节「两组同开」）：前侧两条都卸在后侧那一条之前；丙的卸货命令建在乙的卸货提交之后；采样里丙那一仓在乙那一仓最后一次未锁闭之后才开 | FAIL | `FRONT,FRONT,REAR / 丙命令 ≥ 乙提交 / 采样 乙仓最后 < 丙仓最先` | `REAR,FRONT,FRONT / 乙提交 2026-09-22T02:48:17.5359719+08:00 丙命令 2026-09-21T18:48:13.3990869+00:00 / 采样 乙 [9..9] 丙 [7..7]` |
| 全程一次一扇（REQ-0357）：从车到 11 号站起到旅程走完，模拟器每 100 ms 的采样里任一时刻至多一仓未锁闭；采样确实见过三条需求开过的三个仓 | PASS | `至多 1 仓 / 采样错误 0 / 三个仓都见过` | `至多 1 仓 / 采样错误 0 / 没见过 [] / [] → [1] → [] → [2] → [] → [5] → [] → [5] → [] → [2] → [] → [1] → []` |
| 终态：三条需求各一次装、一次卸、都 Committed、Succeeded，旅程 Completed，八个仓都关门、空、锁上、复位，会话回 Ready | PASS | `Completed / A:Succeeded/ops 2/load 1/unload 1 B:Succeeded/ops 2/load 1/unload 1 C:Succeeded/ops 2/load 1/unload 1 / 八仓 CLOSED/EMPTY/1/0 / Ready` | `Completed / A:Succeeded/ops 2/load 1/unload 1 B:Succeeded/ops 2/load 1/unload 1 C:Succeeded/ops 2/load 1/unload 1 / 不干净 0 仓 / gen 1 / Ready / READY` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
