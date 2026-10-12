# 起点：2026-10-12 09:37:10 只读 RIoT（riot-3cars.ps1，GET only）

方案阶段读的，原始输出当时没落文件，数值照会话里读到的抄录；准备段开始时另读一次存为 01-riot-3cars-prep.txt。

| 车 | actionState | 电量 | batteryState | 位置 | emergencyState | 未结束单 |
| --- | --- | --- | --- | --- | --- | --- |
| agv01 | AT_NA | 8 | NO_CHARGE | 33 | CAN_NOT_RECOVER | 0 |
| agv02 | AT_NA | 47 | NO_CHARGE | 216 | OK | 0 |
| agv03 | AT_NA | 28 | NO_CHARGE | 214 | OK | 0 |

RIoT 未结束单总数 246。agv03 的 28% 低于投运策略 v1 的强制线 30，作为 A 段（投运阈值低电进桩）的起点。
