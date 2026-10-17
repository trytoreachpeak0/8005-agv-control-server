# agv03 车体脱离 RIoT 控制（2026-10-15）

- 12:24 v2 给 agv03 建 PICKUP-3 单（W2G-dd3de04f-…-PICKUP-3，order-2110587613198024704），所属旅程 journey:dd3de04f…|g3。
- 13:56:19 RIoT 报这张单 ORDER_HANG；v2 发 2163 告警「waits for a person」，没有下发任何命令。
- 用户现场说明：「好像是被人推开了，是不是因为长时间避障」——车长时间避障停车，之后被人推开。
- 14:09:54 RIoT 上这张单被取消（ORDER_CANCELLED_IN_RIOT / ORDER_ENDED_WITHOUT_ARRIVAL）。用户：「应该是别人取消的」。
- 14:10:25 起 v2 的重建被挡住：2172 OWN_ORDER_REBUILD_WAITING_VEHICLE，等待原因是 ONBOARD_SESSION_NOT_READY。
- 14:20 用 agv03 自己的凭据读 vehicle-safety 投影：motionState=UNKNOWN，原因码 RIOT_VEHICLE_NOT_ENABLED、RIOT_VEHICLE_NOT_ONLINE、RIOT_BRAKE_NOT_MOVABLE、RIOT_CONTROL_NOT_OK；没有 RIOT_NONFINAL_ORDER_PRESENT。车载端 SafetyStateChanged 报 vehicleStopped=false、unknownPresent=true、VEHICLE_NOT_READY；车屏上没有可用的恢复入口。RIoT 位置 currentPosition=0（不在任何站点）。
- 处理：v2 侧无受治理出口。由人在车上和 RIoT 上恢复车体（刹车、自动模式、启用、上线、重新定位）；恢复后重建单自动派出，车开走。用户选择「现场清人后恢复，让它接着跑」。
- 生产后要评估的两件事（C11 记录）：(1) 车体脱离 RIoT 控制时 v2 没有受治理的出口；(2) 长时间避障停车在看板和告警上不够显眼——2163 告警只说「waits for a person」，没有直接指出车体失控。
- 证据：d2/46、d2/47、d2/48、d2/49、d2/50。
