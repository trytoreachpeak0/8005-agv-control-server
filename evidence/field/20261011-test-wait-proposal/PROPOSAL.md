# IO 接模拟器时的测试等待时间——建议表（草案，供调度与用户商量）

写于 2026-10-11 12:5x，cs#566 续场二。服务端代码 da6e40e2、车载端 c60013ae。只出表，未改任何配置（今天已生效的只有下文「已在用」的两项）。

## 一、实测耗时（今天 C 段，10-10 晚续场）

| 环节 | 实测 | 出处 |
| --- | --- | --- |
| 到站后服务端发出录条码请求 → 自动录入提交 | 约 20～30 秒（12:05:10 到站 → 12:05:40 提交；12:14:14 前到站 → 12:15:09 提交；工具本身启动约 20 秒） | `20261011-W2-cargo-holding/04、10` |
| 模拟器装/卸一扇门（开门 → 设货 → 关门） | 8 秒（工具里两次各停 4 秒），落定再约 1 秒 | `05、09、11-sim-*.txt` |
| 两扇门依次卸货 | 21 秒（12:39:23 → 12:39:44） | `11-sim-unload.txt` |
| 站间行驶 | 215 → N1-3 约 5 分钟（含建单）；N1-3 → N1-4 约 3～4 分钟；N1-4 → 关卡 210 约 3.5 分钟 | C 段时间线 |
| 10-10：离站等待 1 分钟 | B 段录入没跑起来，需求以 `CANCELLED_BY_STATION_TIMEOUT` 结束 | `evidence/field/20261010-W2-staging-to-wire-side/` |
| 10-10、10-11：离站等待 3 分钟 | 自动录入下全部够用（10-10 晚 5 次、今天 2 次，一次成功） | `20261010-W2-resume/SUMMARY.md` |

**测试里真正干等的是两段**：装完货后的离站等待（录完条码、装完货后还要再等满整段，见下表第 1 行），和持货等单（今天让车在站 4 停了 20 分钟）。

## 二、建议调整的项（纯干等，和安全无关）

| # | 配置键（位置） | 生产原值 | 建议测试值 | 理由与风险 | 改法 |
| --- | --- | --- | --- | --- | --- |
| 1 | `JourneyRuntime:stationDepartureWaitTimeout`（服务端 `JourneyRuntimeOptions.cs:38`，下限 5 秒或 0） | 00:05:00 | **00:01:30** | 它同时是取货站录条码的期限，从到站起算（`JourneyRuntimeEngine.cs:2669-2672`）；**装货落定时会从「此刻」重新起算**（`:1380-1382`），所以装完还要再等满一整段才离站，这是 C 段里最长的干等。实测录入 20～30 秒、装货 8 秒，90 秒留出约 3 倍余量。**不能更短**：短于约 60 秒就有以 `CANCELLED_BY_STATION_TIMEOUT` 结束的风险（10-10 用 1 分钟时、录入没跑起来就是这样结束的），那测的就是超时取消而不是装卸。**不要设 0**：0 表示没有期限，录条码会无限等，且装完同一轮就走，失去纠正误放的窗口（`JourneyRuntimeOptions` 文档注释 :35-37）。 | 实例定义可带（`journeyRuntime.stationDepartureWaitTimeout`），或闸门关着手改 + 重启 |
| 2 | `JourneyRuntime:cargoHoldingTimeout`（`:49`，须为正） | 00:30:00（用户新定：生产也最多 00:05:00） | **00:02:00** | 从第一批装货落定起算、不重置；到期以 `CARGO_HOLDING_TIMEOUT` 收尾、带部分货离站（`LoadingPhaseMachine.cs:113-115`）。只在分区允许途中追加时生效（`JourneyRuntimeEngine.cs:4123-4125`）。注入第二条需求本身只要几秒；要测「追加」，追加必须在期限内发生，2 分钟够人工或脚本注入。**要测「持货到期带部分货走」时**，这个值决定等多久，越短越快。 | 同上 |
| 3 | `JourneyRuntime:waitingJourneyWarningAfter` / `…Repeat`（`:108/:111`） | 00:10:00 / 00:05:00 | 不改，或 00:05:00 / 00:05:00 | 只是「等人太久」告警（今天 12:35 的 2163），不影响行为。测试期调短只会多告警。 | **只能手改**（定义不收这两键） |
| 4 | `JourneyRuntime:checkpointWaitBudget`（`:87`） | 00:05:00 | 不改 | 超过只把阻塞原因记成 `VEHICLE_CHECKPOINT_WAIT_EXCEEDED`，不放车、不缩等待。 | — |
| 5 | 车载端 `workflow.operationTimeoutMs`（`Configuration.cs:684`） | 120000 | 不改 | v2 里它只是门开着时的重新提示周期，不会让操作失败（`WireToGateSlotOperationExecutor.cs:31,:1606,:1757`）；调短不省时间，只更早提示、更早报逾期。 | — |
| 6 | 车载端 `workflow.expectedActionOverdueMs`（`:698`） | 3×上项=6 分钟 | 不改 | 只是逾期告警。 | — |

## 三、不能动的（安全相关）

| 配置键 | 原值 | 为什么不能动 |
| --- | --- | --- |
| 服务端 `JourneyRuntime:maximumEvidenceAge`（`:18`） | 30 秒 | 出发安全许可、到站证据、派车事实的新鲜度上限。调长：接受更旧的出发许可；调短（低于心跳与轮询节拍）：许可与事实被拒、站点到期也结束不了。 |
| 服务端 `JourneyRuntime:departureSafetyResultWait`（`:26`，≤10 秒） | 1.5 秒 | 等车载端出发安全答复的时长；调短会让答复在 2 秒有效期过后才被读，延迟出发。 |
| 服务端 `RiotCommands:EmergencyRetry*`（`RiotCommandOptions.cs:26-35`） | 2 秒起、×2、封顶 30 秒 | 急停在确认锁住前无限重试，这里只定节奏。调长：未确认的急停重发得更慢。 |
| 服务端 `VehicleFault:StopProof*`、`MaximumEvidenceAge`（`VehicleFaultOptions.cs:30-53`） | 3 次、0.5～5 秒、3 秒 | 停稳证明的强度。少采样或缩短间隔会削弱证明；收紧会让更多证明被拒并升级为急停。 |
| 服务端 `OnboardSafetyProjection:*`（`OnboardSafetyProjectionOptions.cs:22-39`） | 读预算 2000 ms、车载端超时 3000 ms | 预算须小于车载端超时（cs#573 刚修的就是这里）；耗尽时投影为未知，车载端拿不到新鲜的「已停」，门不能解锁。 |
| 车载端 `vehicleSafety.maximumEvidenceAgeMs`（`:187`） | 5000 | 「车已停」信号的新鲜度窗口，决定能不能给仓门解锁。调长：可能拿陈旧的「已停」在车动时开锁；调短：仓门拒开、告警。 |
| 车载端 `vehicleSafety.requestTimeoutMs`（`:193`） | 3000 | 须与服务端 `OnboardRequestTimeoutMilliseconds` 一致。 |
| 车载端 `workflow.unlockFeedbackTimeoutMs` / `unlockOutputResetTimeoutMs`（`:680/:682`） | 3000 / 3000 | 门锁反馈期限。比模拟器响应短会误报 `SLOT_STATE_UNKNOWN`、会话进 `RecoveryRequired`。 |
| 车载端 `workflow.feedbackStableMs`、`ioSnapshotMaxAgeMs`（`:686/:688`） | 300 / 1000 | 防抖与 IO 新鲜度，直接决定门锁判断。 |
| 车载端 `wireToGate.messageTimeoutMs`、`sessionHeartbeatIntervalMs`（`:275/:290`） | 2500 / 2000 | 有硬约束（须 < 3000），且与服务端会话存活 6 秒写死值配套。 |

## 四、调短会让测试「测成别的东西」的

- `stationDepartureWaitTimeout` 低于约 60 秒：需求以 `CANCELLED_BY_STATION_TIMEOUT` 结束（测成站点超时取消）；装货时门没关就到期会是 `STATION_TIMEOUT_DOOR_NOT_CLOSED`。
- `cargoHoldingTimeout` 短于注入第二条需求所需时间：以 `CARGO_HOLDING_TIMEOUT` 带部分货离站（测成持货到期，而不是追加）。
- `stationDepartureWaitTimeout = 0`：录条码无期限、装完立即走，测不到纠错窗口。

## 五、改法与回读

1. **能放进实例定义的**（19 号部署脚本会写进 `appsettings.Production.json`）：`journeyRuntime.stationDepartureWaitTimeout`、`cargoHoldingTimeout`、`checkpointWaitBudget`、`areaEndAdmissionRevokedTimeout`、`maximumEvidenceAge`、`departureSafetyResultWait`、`pollInterval`（`ParallelInstance.psm1:176-180`）。但定义文件必须是仓库里已提交的（cs#552），测试值若要走定义，需要单独提交一份「测试期定义」，不能临时改副本。
2. **只能手改的**：`waitingJourney*`、`ownOrderRebuild*`、充电相关超时、`RiotCommands`、`VehicleFault`、`OnboardSafetyProjection` 等（定义不收）。做法与今天一致：闸门关、无在途旅程时改文件、重启 V2；不要在运行中改了文件再用 20 号脚本开关闸——它会以 `CONFIGURATION_CHANGED_SINCE_START` 拒绝。
3. **不能热加载**：`JourneyRuntimeOptions` 全部经 `IOptions<>` 注入（48 处），必须重启。
4. **回读**：5350 `EFFECTIVE_CONFIGURATION` 不含等待时长，所以用「重启后读回配置文件的值 + 下一段实际计时」两样证明。
5. **会被悄悄改回**：19 号重装或回滚会把定义里的值写回（定义里是 00:05:00 / 00:30:00）；车载端重新部署会把车上 `appsettings.json` 改回包内默认。
6. **收工改回**：按用户口径，两项改回 00:05:00 / 00:05:00；持货的 00:05:00 与仓库定义里的 00:30:00 不一致，要么另开票改定义，要么每次部署后手改。
