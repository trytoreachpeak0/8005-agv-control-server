# agv02 送完货停在卸货站不动：只读取证（2026-10-15 16:1x）

原始：65-agv02-journeys.txt、66-agv02-purpose-config.txt、67-agv02-purpose-records.txt、68-v2-idle-charging-config.txt、69-agv02-now.txt、70-agv02-onboard-1558.txt、agv02-journal-copy-1610/（日志簿复制件 + 15:58–15:59 车载日志，车上原件未动）、64-agv02-after-trip1-log.txt、34-trip-watch.txt（位置时间线）。全部只读。

## 现在这一次（用户看到的那次）：不是派车问题，是卸货没结束
- 旅程 e9bd5f2c|g2，Stage=AwaitingUnloadResult，UpdatedAt 07:59:38.98Z，车在站 7（trip-watch 15:59:53 起 agv02@7 IDLE），需求 e80362ae 仍是 LOADED（slot 3）。
- 车载日志：15:59:39.340「已向物理3号仓发送开锁脉冲触发，DO地址=102」；15:59:39.497「3号仓IO变化：DO 1->1，锁DI 1->0，光幕DI 0->0」；15:59:39.950「DO 1->0，锁DI 0->0」。之后 3 号仓再无 IO 变化（到 16:08:13 段末）。
- 日志簿 RecoveryState（07:59:39.27）：unsettledSlotOperationAttemptId=51eac174-de46-4655-b56a-f0fefd8c5f17，activeUnlockSlots=[3]，operationContext=UNLOAD（operationType 1）demand e80362ae slot 3 expectedOccupied=false。
- 15:59:41 起每 2.5 s「忽略并发重复SlotOperationCommand：attempt=51eac174…」（到取证时 198 次）——服务端重发卸货命令，车上判定同一操作仍在进行。
=> 车上的卸货操作在等 3 号仓门重新锁上（锁DI 回 1）并核空；门开着没关，卸货结果不上报，服务端旅程停在 AwaitingUnloadResult，VehiclePurposeClaims 仍是 TRANSPORT（e9bd5f2c|g2），派车轮不会给它空闲返回、充电或新单。属于三种可能之外的第四种：现场门没关。

## 今天早些时候唯一一次真正卸完货（75e648c4）
- 06:55:24Z 释放 TRANSPORT（ReleaseReason LAST_DEMAND_UNLOADED），旅程 Completed 06:55:26。车 14:54:41 已到站 49（计划第 6 站），之后停在 49。
- 06:58:31.83 新 TRANSPORT 旅程 f581a393 建立（取货 217），06:58:32 RIoT 下单，14:59:00 车离开 49。空档 3 min 07 s。
- 这段时间 agv02 没有任何 IDLE_RETURN、CHARGING 用途记录（今天 agv02 全部 24 条用途记录都是 TRANSPORT；全车队只有 agv01 有过 1 次 CHARGING）。电量 59%（WaitingBatteryPercent），充电策略强制线 30%、完成线 80%，不会触发充电。
- 服务端 Information 级日志在 06:53:30–06:59:30 没有派车轮/空闲返回的记录（派车轮判定不在 Information 级），无法从日志区分「等派车轮周期」与「空闲返回延时」。配置里没有空闲返回延时项（appsettings.Production.json 只有 JourneyRuntime.pollInterval=00:00:02、clearanceToWaitingPointEnabled=False，见 68）；等待点 214/215/216 无车辆范围限制；agv02 fleet：allowedTaskTypes=STAGING_TO_WIRE、zones=WIRE_BONDING；DispatchZoneParameters 两区 EnRouteAdditionMaxPathCostIncrease=65000、StarvationThresholdSeconds 空。
- 其余旅程之间多数间隔 3～7 s（立即续派），较长的空档：03:33:17→03:41:30（8 min）、06:24:40→06:29:31（5 min）、06:33:24→06:35:31（2 min）、06:55:26→06:58:31（3 min）——这些都是 TRANSPORT 结束后到下一条 TRANSPORT，期间没有 IDLE_RETURN。是否因为 STAGING_TO_WIRE 需求暂时为空需看当时 MES/看板积压，本次未取（看板积压只有现时值）。

## 附带发现
- VehiclePurposeClaimRecords 75e648c4 的 ReleasedAt 写成「2026-10-15 14:55:24.1857617+08:00」（本地偏移），其他行都是 +00:00。按字符串比较/排序时间的查询会把这一行排错 8 小时。
