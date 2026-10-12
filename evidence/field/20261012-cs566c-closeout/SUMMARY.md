# cs#566 续场三收工（2026-10-12 12:09～12:12）

| 项 | 结果 | 出处 |
| --- | --- | --- |
| 闸门 | 12:09:18 关闸，为 false | `../20261012-cs566c-contention/18` |
| 两车栈 | agv02、agv03 的车载端与模拟器都已停，进程数 0；IO 仍指向模拟器 `127.0.0.1:1502` | `../20261012-cs566c-contention/19-*`、`03`、`04` |
| 服务端配置 | 等待改回 5:00/5:00；`VehicleFaultRecovery.enabled` 改回 false；`onboardClearanceEntryDeclared` 改回 false。与开测前备份 `bak-20261012093949-before-cs566c-prep` 逐键比对，**零差异** | `01`、`05` |
| 凭据变量 | V2 服务注册表 `Environment` 已删除 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，回读确认不存在（只记键名）。控制端 DPAPI 里那一份保留 | `01` |
| agv03 车载端 | `unableToChargeEntryEnabled` 改回 false，`recoveryResumeEnabled` 一直是 false；用开测前备份覆盖后，SHA-256 前 16 位 `3027BAADE8A579A3`，与开测前一致 | `03` |
| agv02 车载端 | 本次没改它的配置 | — |
| 名册与策略 | 名册空（version 7）；策略 v1（30/80） | `01` |
| 残留 | 未恢复的桩暂停 0、车辆资格暂停 0、211 独占 0 行、未结束周期 0 | `02` |
| V2 | 停服，启动类型 Manual；MVP 前后均为 Running、pid 10912（中途 pid 变化来自 agv01 运维会话的重启，调度已核） | `01` |
| 最后一次只读 RIoT（12:11:32） | agv01 82%、位置 0、1 张未结束单（用户在用，未碰）；agv02 216、80%；agv03 215、81%；agv02 和 agv03 都没有未结束单 | `06` |
