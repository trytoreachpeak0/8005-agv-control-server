# agv01 充电单 orderState=2 的含义（只读核对，2026-10-11 20:3x）

读到的：20:09:51 `detailByOrderId/order-2109213792419315712`（agv01，upperId `W2G-CHARGE-b6dd053c-1536-4352-851d-13fcf073adf4-1`，25 号图 212→211→act 78）返回 `"orderState": 2`（`05-agv01-precheck.txt`）；同一时刻 agv01 的非终态单列表为空。

2 的含义：**CANCELLED，终态**。出处：
- `8005-agv-program/rcs/riot-behavior-lab/evidence/rounds/2026-07-20-round-9/execution-log.md` 第 21、28、42 行：`CMD_ORDER_CANCEL` 成功后「很快 `orderState=2 CANCELLED`，车 `IDLE`」，「`orderState=2`，`missionState=4`，`procState=IDLE`」；
- 同目录 round-13 第 13 行、round-14 第 57 行（cancel → `orderState=2`）、2026-07-21-round-24 第 22 行（「已到 act 后被用户取消 → `CANCELLED(2)`」）；
- V2 代码 `src/ControlServer.Application/RiotOrderCommandPorts.cs` `RiotOrderState.Cancelled = 2`；`ForeignRunningOrders.IsExplicitlyEnded` 把 Cancelled 算作明确结束；查非终态单用的过滤是 1、3、7、8、9、10，不含 2。

结论：这张单已终结，不会再让 agv01 去 211。
