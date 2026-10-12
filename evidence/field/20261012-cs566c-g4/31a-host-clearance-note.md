# C 段确认清桩用的是 Host 入口，不是车载屏入口

- 用的是 `POST /api/charging/v1/station-clearances`（`ChargingStationEndpoints.ClearAsync`），Bearer 是恢复凭据（键名 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，值不记），来源记为 `Host`。调度 C10 选的「甲」。
- 没用车载屏入口，原因是 agv03 没有 `CONTROL_SERVER_RECOVERY_PROOF`：车载端 `270c20d0` 的 `WireToGateBusinessService.StationClearance.cs:ReadStationClearance` 要求 `CanUseRecoveryOperator(requireProof: true)`，不满足判为 `NoVerifiedMaintainer`，按钮 `ConfirmStationClearance` 不显示（截图 `30-hmi-capture-clearing/`）。
- **更正**：上午说「清桩入口不依赖两个开关」——它确实不依赖 `recoveryResumeEnabled`／`unableToChargeEntryEnabled` 这两个开关，但依赖同一个恢复凭据。那句话只说对了一半。
- 车载屏入口昨天已在 agv02 上验过（G3，`20261011-W3-cannot-charge/22`）。agv03 缺恢复凭据属部署缺口，调度另记跟进。
