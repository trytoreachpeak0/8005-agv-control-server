## 生产切换（2026-10-14 晚）第 19 步、新库 B 重导受治理事实、agv02 第 20～21 步、换装 s3.runtime-on

证据在控制端 `F:/8005-scratch/cutover-ev/production-20261014/`，逐仓核对记录在 `8005-workspace-v2/evidence/field/20261014-req0263-slot-verify/`。

**第 19 步（18:05）**：19 号 `-ProductionCutover` 装生产段 S1 运行时关，包来自 run 38542183893（`f12bc809` + `d4957461`）。
- 服务端核对：`EMERGENCY_STOP_RELEASE=enabled=true credential=set loadedCredential=set loadedConfirmers=1`；两个解除入口不带凭据探测，都是 401。
- `EFFECTIVE_JOURNEY_BINDING` 核对通过（5m/5m，`slotConfigurationReadinessGate=Enforcing`）。
- 实读 `appsettings.Production.json`：`stationDepartureWaitTimeout=00:05:00`、`cargoHoldingTimeout=00:05:00`、`stationTimeoutRedispatchLimit=2`。
- MVP 前后都是 Stopped/Disabled。

**重导受治理事实（18:11）**：先 dry-run，再正式导入。导了 26 号图站点目录、`seed-approved-facts`、三车 `bind-io`、等待点 v1、充电桩名册 v1、充电策略 v1（批准并激活）。三车 `slotModelVersionId=51caf7a3a1a241a1beb8d8269a1744e0`。FieldOps 用的是 `fieldops-96a88db0`：它到 `f12bc809` 之间没有迁移改动，FieldOps 只改了注释。

**车载端重装**：三车原装的是 `91766e38`，生产包是 `d4957461`（多出 hmi#343 和 SDK 8.0.426）。C11 定每车先用 06 号重装：`-RunId 38542183893 -OnboardPort 58105 -HealthPort 58107 -UseSiteIo`，09 号就不用跑了。

**agv02**：
- 06 号重装（用户授权）后读回 `onboardBuildCommit=d495746…`、IO `192.168.71.150:502`；车上暂存目录已删。
- 逐仓核对三轮：
  - `run1`：0/8，操作失误。放进仓里的物体没有取出，八仓都判「取出后光幕没回无物」，逐帧轨迹里光幕 1→0 之后一直是 0。记录没提交。
  - `run2`：只为把物体取出来，记录没提交。
  - `run3`：8/8 三项全过。锁反馈打开 75～86 ms，DO 复位 512～523 ms。
  - 前两个目录原样留着当证据。
- FieldOps `verify`：`OK`、`ready:true`、`SLOT_CONFIGURATION_READY`、`slotsConfirmed:8`。
- 19:46 起栈，带 `-NoSimulator`，用户授权。日志 `SessionReadiness READY`；车上到 58105、58107 的连接都是 Established。

**一个陷阱**：W1 探针从 Git Bash 启动时，调用的是 Git 自带的 ssh（msys），会把约 8000 字符以上的参数截断，报 `-EncodedCommand is not properly encoded`。改从 PowerShell 或 Terminal 面板启动，用的是 System32 的 OpenSSH，就没问题。已写进 traps。

**换装 s3.runtime-on（19:50）**：19 号 `-ProductionCutover`，闸门仍关。纯只读回读：
- 闸门：`RiotCreateDispatch.enabled=False`。
- 受理：`AcceptedDemands` 0；新库 B 从没导过区表，读码确认等同于不受理。
- 车队：三行，`JourneyRuntime.enabled=True`。
- 按三车车队读回等待点、充电策略、充电桩名册，都正常；`task-type-stations` 已 ACTIVE v1。
- 没有 OrderIntents，没有 Warning/Error。
- RIoT 电量 19:51：agv01 71、agv02 61、agv03 32（17:47 时是 89、79、50）。
