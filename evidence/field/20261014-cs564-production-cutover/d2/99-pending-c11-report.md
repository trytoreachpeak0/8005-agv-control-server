# 待补发给 C11（10:27 起 C11 会话不可达）

1. 10:23 用户决定跳过三车重核，沿用 10-14 的逐仓核对。原话：「可以跳过的，因为昨天核对过了」。
   - 原记录没有改动。另外生成了三份复用记录，放在 agv0N-dbC-reattest/，只改了三处：slotModelVersionId 换成 74da3fee…；fieldRecordReference 指回原目录；每个仓的 note 写明来源、原记录 SHA-256、旧车型 id 和批准时间。
   - 复用记录交给 verify，三台车都返回 OK、ready=true（d2/26）。
   - 在这之前，用原记录直接 verify 被拒：「Vehicle model version 51caf7a3… has no slots」（d2/25）。
2. 快路径起服失败：多车实例在空库上起服，会被启动检查拒绝，原因是 WAITING_POINTS_FEWER_THAN_VEHICLES。要在服务停着的时候先导入事实（等待点），再起服（d2/21、d2/22）。
3. 10:27 三车起栈，都 READY gen=1（d2/28）。
4. 10:31 只读读了一次 MES（catalogRevision 47644）。还在目录里、又在任何旧库被受理过、并且在 RIoT 上建过单的需求，只有 Q26069Y01-1（T8-8），已从区表剔除（d2/30）。
   - 导入全量区表（不含 T8-8）v1，共 315 行（d2/31）。
   - 三条旅程：agv02 452e15a4 是 STAGING_TO_WIRE，4 条 5 篮；agv03 dadde6f1 是 DIE_TO_WIRE_STAGING，5 条 5 篮；agv01 6cf1c78a 是 WIRE_TO_NITROGEN，3 条 6 篮。
5. 10:34 用户授权开闸，带 d2/io-check.json。三张 PICKUP-1 都是新建的，CreateAttemptCount=1，CreateResponseAccepted，单号是新的 order-21105598…（d2/35）。
6. 10:41:09 agv02 在 217 第一次站点超时，锚需求 Q26101454-3 直接作废（CANCELLED_BY_STATION_TIMEOUT），并报人工搬运告警 STATION_TIMEOUT_ANCHOR_NOT_RELEASABLE（事件 2311）。锚需求超时一次就作废，不给第二次机会。agv01 在 76 的 Q26101668-5 也已作废。用户原话：「不扫也没事的，因为这个刚上线，让车子继续跑吧，需求作废很正常的」。
7. 10:41～10:50 agv01 卡在 AwaitingDepartureSafety，原因是 PRE_DEPARTURE_SAFETY_NOT_VALID。车上的检查结果本身是 SAFE，但车载时钟比 factory01 快 +1041 ms（实测），超过 clockSkewToleranceMs=500，而结果的 validUntil 只有 2 秒。10:33 w32time 才同步过，10:44 已经快了 1 秒。w32tm /resync /force 没能把偏差拉回来（只做慢调）。10:5x 经用户授权用 Set-Date 步进调整，从 +1028 调到 -60 ms（d2/40、d2/41、d2/42）。agv02 的偏差是 -176 ms，agv03 是 +45 ms。
8. 10:40:19 和 10:40:32 有两次车载连接以 10054 断开；agv03 到 10:42 已经是第 4 代会话。行驶中断线重连属于已知现象，需要持续观察。
