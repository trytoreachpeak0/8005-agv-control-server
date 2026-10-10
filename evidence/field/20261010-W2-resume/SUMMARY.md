# cs#566 续场小结——2026-10-10 22:54 ～ 10-11 00:48，agv02

## 结果一览

| 段 | 结果 | 证据 |
| --- | --- | --- |
| 解除人工充电保持（F 段的一步） | 通过：用户在车载屏按「充电后返回服务」，23:15:13 `RETURNED_TO_ELIGIBILITY_EVALUATION`，`ManualChargingHolds` 行删除、记录写上解除时刻与请求号 | `06-hold-released.txt` |
| F 完整充电周期 | 未做（用户定）：车已被人工充到 74%，高于强制线 30%，做完整周期需另批临时策略并长时间等待 | — |
| B STAGING_TO_WIRE 装侧（重跑） | 通过：217 装、站 15 卸都是 `[5]`（后侧组最小） | `../20261010-W2-staging-to-wire-side-rerun/` |
| A2 混挂站一次停靠两侧（第一次） | 部分：追加并入同一停靠通过；乙因假 MES 卡顿被 `SUBLOT_BOX_COUNT_UNAVAILABLE` 拒收后站点超时 | `../20261010-W2-mixed-side-a2/` |
| A2 重跑 | 通过：同站一次停靠甲 `[5]`、乙 `[1]`，关卡先前后后，两条 `UNLOADED` | `../20261011-W2-mixed-side-a2-rerun/` |
| D 产品急停（cs#336） | 通过（按调度范围）：`triggerEmergency` 一次、门锁恢复后自动 `cancelEmergency`；收尾走现场兜底（停 V2、RIoT 结单） | `../20261011-W2-product-estop/` |
| C 持货等单追加另一站 | 未做（时间）；「持货等待中追加」已在 A2 两轮出现，但追加的是同一站 | — |
| E 空闲返回与等待点 | 未做（时间） | — |

## 开始时的实读（`00-state.txt`）与交接不符之处

- 车不在 (-27093,28166)、26%，而在站 214、74%：用户确认是现场挪车并人工充电。
- 交接「乙：人工充电后直接做 B 等」走不通：人工充电保持在库里时服务端不受理任何新用途（`ChargingStandingCriterion`，`VEHICLE_IN_MANUAL_CHARGING_HOLD`），电量回升不解除。
- 「V2 实例现有的恢复管理员凭据」不存在；服务端处理这条消息时核 `administratorRole`（`WireToGateStore.cs:636`）与会话就绪，不核 proof；proof 只在车载端本地检查是否已配置。

## 恢复入口：开与关（`04`、`09`～`11`）

- 23:10 只改 agv02 车端：`recoveryResumeEnabled=true`，本机生成随机值写入 `CONTROL_SERVER_RECOVERY_PROOF`（机器级），值未离开车、未进证据与聊天；没有改 V2 服务端环境（`14-set-recovery-window.ps1` 的服务端那一半未用）。
- 00:44 关回：`recoveryResumeEnabled=false`，变量已删除。关回后 `appsettings.json` SHA-256 `0D809DD1…5DC59F` 与改动前备份 `appsettings.json.bak-20261010231034-before-recovery-entry` 完全一致；开着时是 `2729DF06…65BF72`。

## 条码自动录入（`16-enter-sublot-uia.ps1`，8005-workspace main 3fedf656，用独立 worktree，没有 pull 共享检出）

- 共 5 次实车提交，全部 `SUBMITTED_ONCE`、回读一致；一次在请求发出前被工具按 expectedSublots 拒绝（未写入）。首用用户在屏前看着。
- 火绒：agv02 上没有火绒的服务或进程；计划任务拉起的 pwsh 在会话 1 正常运行（`07-uia-probe-locked-unlocked.txt`，以及上一轮 `07-task-battery-probe.txt`）。
- 锁屏：锁屏时只读探针仍能找到窗口、条码框和「手动提交」；**锁屏下写入未核实**（当时没有录入请求，框是禁用的），本晚所有实际录入都在未锁屏时。

## 测试侧失误与已知现象

- B 段漏做模拟器关门，车在 217 多停约 9 分钟（`STATION_TIMEOUT_DOOR_NOT_CLOSED`，服务端既不取消也不放车）；之后改用 `cs566-sim-respond.ps1` 代现场人员做装卸。
- cs#573 复现一次（A2 重跑 00:16:55），离站多等约 4～5 分钟；按用户要求报调度提前处理，调度答复已是最优先服务端票，批次 10 出口解冻后合入。
- hmi#291、hmi#293 本晚没有收到现场报告（没有专门盯屏核对）；hmi#292（车载端日志乱码）同类现象见于 ssh 控制台中文输出。

## 服务端日志缺失（调度要求写明）

**2026-10-10 23:24:11 ～ 24:00 之间 V2 服务端没有日志。**`controlserver-20261010.ndjson` 23:24:11 写到 1,073,742,032 字节后停写：appsettings 的 Serilog File sink 没设 `fileSizeLimitBytes`／`rollOnFileSizeLimit`，按默认 1 GiB 封顶后不再写。B 段与 A2 第一次的排查因此只能靠库与协议收发表。调度已开 cs#587（切换门槛级）。10-11 的文件按当时速度约 02:30 会再满；V2 已于 00:42 停止，今晚不再增长。

## 范围外问题（已报调度）

1. cs#587：V2 日志 1 GiB 停写（见上）。
2. 假 MES（`127.0.0.1:58188`）响应时快时卡：00:06 实测 5 次一次 20 秒超时、其余 0.7～2 秒；直接导致 A2 第一次的拒收。调度定：测试设施问题，同一需求至多重录一次。

## 收工状态

| 项 | 状态 |
| --- | --- |
| V2 服务 | **Stopped，启动类型 Manual**（用户授权）；配置里 `RiotCreateDispatch.enabled=true`（关闸被拒）；库里有带故障的未结束旅程 `journey:d0000566-…` |
| MVP 服务 | Running，pid 15624，全程未动 |
| RIoT | 无涉及 agv02 的非终态单（00:48:07） |
| agv02 车 | (-47720,4343)，IDLE，急停 OK，电量 56%，空载；现场停好 |
| agv02 车载电脑 | 现场已关机；关机前车载端与模拟器已停，恢复入口已关回 |
| 充电名册 | v3 空（本晚未改） |
| 人工充电保持 | 已解除（23:15:13） |
| 测试期临时等待值 | **未改，仍是**离站等待 00:03:00、持货 00:03:00（原 00:05:00、00:30:00）——进生产前改回 |
| agv02 IO | **仍指向模拟器** 127.0.0.1:1502（备份 `appsettings.json.bak-20261010181927`）——未改 |
| 车上残留 | `C:\8005\uia\` 下本会话的测试小脚本（`cs566-sim*.ps1`、`agv-uia-probe.ps1`、`uia-probe-launch.ps1`、`probe-*.json`）与 `%USERPROFILE%\cs566-*.ps1`：车载电脑 00:45 后 SSH 不通，未能删除；不含凭据 |

**下次启动 V2 之前**：先处理闸门配置与 `journey:d0000566-…`（按调度 10-10 定，下次部署含 cs#44 时换新库；不在旧库上直接启动），启动类型改回 Automatic。
