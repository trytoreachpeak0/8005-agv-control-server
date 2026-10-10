# B 段重跑：一趟 STAGING_TO_WIRE 按目的机台装侧——2026-10-10 晚 agv02

## 结论

**走通。**目的机台 N1-16 属后侧，217 装货与站 15「N1-15_N1-16」卸货的目标仓都是 `[5]`，即后侧组（5～8）中编号最小的仓；需求 `UNLOADED`，旅程 `Completed`。条码由自动录入工具（UIA）填写，一次提交成功，用户在屏前看着。

一处测试侧失误：装货时没有在模拟器上补做「放花篮、关门」（上一轮 A 段每次都做，`../20261010-W2-mixed-side/09-sim-load-bing.txt`），车在 217 多停约 9 分钟，原因码 `STATION_TIMEOUT_DOOR_NOT_CLOSED`。门开着期间服务端既不取消需求、也不放车离站；23:37:45 关门后照常继续。

## 身份

| 项 | 值 |
| --- | --- |
| 服务端 | v2 并行实例 fp/v2-impl@2fe490d7，准入 2；离站等待 3 分钟、持货 3 分钟（测试期临时值） |
| 车载端 | agv02 w2g/fp-v2-impl@ca89ef8f；IO＝模拟器 127.0.0.1:1502；`recoveryResumeEnabled=true`（本晚临时，见 `../20261010-W2-resume/`） |
| 需求 | b1000566-0000-0000-0000-000000000002，STAGING_TO_WIRE，条码 W2-1010-B2-S2W，区号 N1-16 |
| RIoT 订单 | W2G-b1000566-0000-0000-0000-000000000002-PICKUP-1、-GATE-1 |
| 授权 | `00-authorization.md`，原话「可以」 |

## 时间线（北京时间）

| 时刻 | 事件 |
| --- | --- |
| 23:23:13 | 段前实读（`01-precheck.txt`）：车在 214，电量 70%，会话 READY，闸门关，无在途、无故障、无人工充电保持 |
| 23:23:22 | 注入需求；23:23:35 受理，等闸门（`PICKUP_CreateDispatchDisabled`） |
| 23:24:12 | 开闸（`03-gate-open.txt`），V2 重启，MVP pid 15624 不变 |
| 23:24:39 | 车离开 214；路上 `ONBOARD_SESSION_NOT_READY`（已知：真车载端挂着本服务端在途单时整段未就绪） |
| 23:24:56 | 服务端 SublotEntryRequested（expectedSublots [W2-1010-B2-S2W]） |
| 23:26:01 | 车到 217，`AwaitingSublot` |
| 23:26:29 | 自动录入提交一次，回读一致（`04-auto-entry.json`） |
| 23:27:19 | `AwaitingLoadResult`，5 号仓门开 |
| 23:28:34 | `STATION_TIMEOUT_DOOR_NOT_CLOSED`（测试侧漏做模拟器关门） |
| 23:37:38～45 | 模拟器 5 号仓置有货、关门（`05-sim-load-slot5.txt`） |
| 23:37:46 | Load 提交，需求 `LOADED`；持货等待开始（`CargoHoldingStartedAt` 15:37:48Z） |
| 23:41:58 | 持货 3 分钟到，`LoadingPhaseState=CLOSED`，车离开 217 去站 15 |
| 23:44:24 | 车到站 15，`AwaitingUnloadResult`，5 号仓门开 |
| 23:44:51～58 | 模拟器 5 号仓置空、关门（`06-sim-unload-slot5.txt`） |
| 23:45:36 | 需求 `UNLOADED`，旅程 `Completed` |
| 23:46:02 | 关闸 PASS（`07-gate-close.txt`），MVP pid 15624 不变 |

## 判据

| 判据 | 结果 | 依据 |
| --- | --- | --- |
| 217 Load 落在后侧组且为该组编号最小的可用仓 | 通过：`[5]` | `08-verdict-db-readonly.txt` StationOperations Load；SlotModelSlots 5～8 为 REAR |
| 站 15 Unload 同上 | 通过：`[5]` | 同上，Unload |
| 需求 `UNLOADED`、旅程 `Completed` | 通过 | 同上 |
| 无需人工处置 | 模拟器上的放货、取货、关门是测试替代现场人员的动作，不算处置；漏做关门是测试侧失误 | — |

## 条码自动录入（首次实车使用）

- 控制端先核对条码在服务端这次请求的 expectedSublots 里，再经计划任务进车上桌面会话（会话 1）录入、回读、提交一次；结果 `SUBMITTED_ONCE`，`autoEntered=true`、`method=UIA`。服务端把这次录入记为 KEYBOARD（协议没有「自动」这个取值），结果文件是唯一的自动录入标记。
- 控制台输出里的中文（站名、窗口名）显示为乱码，是 ssh 输出的编码问题；条码本身是 ASCII，不受影响。
- 锁屏与火绒两项核实见 `../20261010-W2-resume/07-uia-probe-locked-unlocked.txt`。本段录入时屏幕未锁。

## 文件

`00` 授权；`01` 段前实读；`02` 注入；`03` 开闸；`04` 自动录入结果与控制台；`05`、`06` 模拟器装卸；`07` 关闸；`08` 判据读库；`09` 监看记录（日志行的时刻是监看打印时刻，开闸重启后曾把整份旧日志重读一遍，2108、2205、2242 等行是 20:1x 的旧事件，不属于本段）。
