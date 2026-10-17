# cs#723：agv02 07:40 重按被拒只读取证（2026-10-15）

原始：d2/62-cs723-0740-raw.txt（v2 库只读，ro-sql.ps1/sqlite-tools 副本，查询时刻 07:45:57Z）；d2/62-cs723-0740-outbox-summary.txt（发件箱摘要）；d2/agv02-journal-copy-0745/（agv02 日志簿复制件与 15:37–15:42 车载日志，车上原件未动）。

## ① JourneyDemands，旅程 e9bd5f2c（07:45:57Z 读取，RemovedAt 为当前值）
| DemandId | PickupStopId | Status | AddedAt | RemovedAt / Reason | Target |
|---|---|---|---|---|---|
| e9bd5f2c（锚） | e9bd5f2c\|g2\|PICKUP | TERMINATED | 07:33:33 | — | [1] |
| 67e7c463 | 67e7c463\|g2\|PICKUP | TERMINATED | 07:33:40 | — | [5,6] |
| 68797314 | **67e7c463\|g2\|PICKUP**（同站） | PENDING_LOAD | 07:33:43 | 07:43:49 RELEASED_AT_STATION_DEADLINE | [7,8] |
| 7bd4ba48 | 7bd4ba48\|g2\|PICKUP | PENDING_LOAD | 07:40:18 | — | [1,2] |
| e80362ae | 7bd4ba48\|g2\|PICKUP | LOADED | 07:40:21 | — | [3] |

=> 07:40:17 取消 67e7c463 时它**不是本站最后一条**：同一 PickupStop 上 68797314 一直挂到 07:43:49 才按站点期限释放。
RecoveryWorkflows：6828730c-be48-bf5f-8959-90d68c80ff78，demand 67e7c463，RequestMessageId e5281821-8ef1-47dd-8d13-18b5275eec83，Created 07:40:17.247，Reconciled ALL_EMPTY 07:40:17.342。

## ② 服务端 ProtocolOutbox 07:38:00–07:41:59（该时段只有 agv02 的行，Heartbeat/VehicleSafety 除外）
- 07:38:41.196 CurrentStopWorklistSnapshot rev 148，items []，stopEndedReason STATION_DEADLINE_EXPIRED（锚 e9bd5f2c 那一站结束），ack 07:38:41.287
- 07:38:43.887 UpcomingStopPlanSnapshot rev 216，ack .915；07:38:43.903 PreDepartureSafetyCheck
- 07:38:47.031 UpcomingStopPlan rev 217；07:38:47.186 VehicleBusinessStateSnapshot
- **07:38:47.191 CurrentStopWorklistSnapshot rev 149**，operationSessionId 079bbae8-6c9c-2d55-b499-7fec31bf271c，items [67e7c463/Q26101897-2/PICKUP, 68797314/Q26091200-3/PICKUP]，ack 07:38:47.242
- 07:38:47.195 UpcomingStopPlan rev 218；07:38:47.198 SublotEntryRequested rev 149（未 ack，属正常 request）
- 07:40:17.590 UpcomingStopPlan rev 219；07:40:20.419 rev 220；07:40:23.548 rev 221
- **07:40:17 取消落定后到 07:41:59 没有任何 CurrentStopWorklistSnapshot**（07:40:18/21 加入 7bd4ba48、e80362ae 时也没有）。
完整 items 与 payload 见 62-raw。

## ③ 车上（日志簿复制件 07:45 + 车载日志）
- 车载日志 15:38:47.23 起每 2.5 s「服务端请求录入Sublot … revision=149，expectedSublots=Q26101897-2、Q26091200-3」，15:40:26.45 一行「恢复向量LOAD_CANCELLATION未执行：reason=BUSINESS_ID_CONTENT_CONFLICT」，之后 15:40:28 起仍是 revision=149 同一清单，连续 55 行到 15:42 段末。
- 日志簿只保存最后一次应用：现为 CurrentStopWorklistSnapshot rev 153（AppliedAt 07:45:27.35），UpcomingStopPlan 224（07:43:55），VehicleBusinessState 88；RecoveryState 07:45:27 pendingLoadCancellation=null。07:40 时段车上没有比 149 更新的 worklist（由日志确认，日志簿不保历史）。

## 结论
与 07:13 同一原因：取消 Reconciled 后服务端只发 UpcomingStopPlanSnapshot，不发新的 CurrentStopWorklistSnapshot；车上 rev 149 仍列已终止的 67e7c463（Q26101897-2）→ 9 s 后重按 → 同 cancellationId 新 verifiedAt → 1105 拒。这次被取消的不是本站最后一条（68797314 同站仍在），所以「最后一条才不推」不是触发条件。
