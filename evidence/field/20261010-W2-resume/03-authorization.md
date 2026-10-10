# 续场授权与决定（2026-10-10 晚，cs#566）

## 23:1x 用户在现场会话窗格里的回答（AskUserQuestion）

| 问题 | 用户选择 |
| --- | --- |
| agv02 的人工充电保持怎么处理 | 「授权解除 (Recommended)」：改 agv02 车载配置（打开 `recoveryResumeEnabled`、本机配随机凭据），启动车载端完整栈，闸门保持关，由用户在屏上按「充电后返回服务」；收工前关回、删凭据 |
| 解除之后做哪些段 | 「B→A2→C→E→D，F 不做完整周期 (Recommended)」：充电段只记录按钮解除保持这一步 |
| 车与时间 | 「是，做到 01:00 左右」：车由现场挪到 214 并人工充电 |

这一条授权覆盖：改 agv02 车载配置、启动车载端完整栈（「Ask first」第 1 类）、用户按按钮。**不覆盖开闸与任何一段动车**，每段另问。

## 与交接说法不同之处（详见 `00-state.txt`）

- 交接的「乙：人工充电后直接做 B、A2、C、E、D」走不通：人工充电保持在库里时，服务端对这辆车不受理任何新用途（`ChargingStandingCriterion`，原因码 `VEHICLE_IN_MANUAL_CHARGING_HOLD`），电量回升不解除。
- 「配上 V2 实例现有的恢复管理员凭据」：V2 实例没有配置恢复管理员凭据；服务端处理 `ManualChargingReturnToServiceRequested` 时核对 `administratorRole` 必须是 `MAINTENANCE_ADMINISTRATOR` 或 `SYSTEM_ADMINISTRATOR`（`WireToGateStore.cs:636`），也看会话是否就绪，但不核 proof；proof 只在车载端本地检查是否已配置，不随消息发送。（措辞经调度 Coordinator 10 于 10-11 00:1x 更正。）因此只在 agv02 本机生成一个随机值写入 `CONTROL_SERVER_RECOVERY_PROOF`（机器级），值不进证据、不进聊天。
- 不用 `14-set-recovery-window.ps1` 的服务端那一半：它会给服务加恢复凭据并重启服务（默认服务名还是 MVP 的 `8005 AGV ControlServer`），把服务端的管理员恢复一并放开，超出这次需要的范围。只照它的车端那一半做。
