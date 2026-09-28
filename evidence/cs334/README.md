# cs#334 证据：持锁期间车载端写没有上限

票：trytoreachpeak0/8005-agv-control-server#334。全部在本机回环与单元夹具上做，没有碰 agv01、factory01 与 RIoT。

每条都标了「读到的」（跑出来或读代码读到的）还是「推的」。

## 第一步：用机器找持锁期间的 I/O（step1/）

**做法**（读到的，`step1/gate-probe.patch.txt` 与 `step1/GateProbe.cs.txt`，临时探针，没有进任何提交）：

- 在持锁入口打一个跟随异步流的标记：`JourneyRuntimeWorker` 的整轮、`JourneyRuntimeEngine.ExecuteOnceAsync`、`DemandReleaseService.RunOnceAsync`、`RouteGraphRefresher.RefreshOnceAsync`，以及故障清除五处 `TryEnterAsync` 拿到锁之后。引擎的测试不经工人、直接调 `ExecuteOnceAsync`，而生产里它只被工人在锁内调用，所以在入口打标记与生产等价。
- 在所有 RIoT、MES、`IOnboardPeer`、`IRouteGraphSource` 端口的消费方用 `DispatchProxy` 包一层，在 `OnboardPeerConnection.SendAsync` 与全部 `HttpClient` 请求上记录，带着标记就记下调用栈。
- 全量跑一遍（`step1/probe-full-run-result.txt`：2855 条全过），`step1/summarize.py.txt` 按「谁持锁、调了什么、第一个非传输层的调用方」归类，结果在 `step1/probe-full-summary.txt`。

**结果**（读到的）：

| 持锁期间的 I/O | 次数 | 上限 |
| --- | --- | --- |
| 车载端发送（引擎整轮 15 个调用点、释放服务的 `JourneyClosure.SendAsync`） | 10764 + 72 | **修前没有** |
| RIoT 读写（读车、读图、外来单、建单与对账、急停、Hold、运动采样、路网） | 29809 | `RIoT:timeoutSeconds`，默认 30 s，不重试 |
| MES 需求目录 `IMesIngestCatalog.ReadCatalogAsync` | 4746 | `HttpClient` 没设，按 .NET 默认 100 s（推的，依据是 `Program.cs` 没设值） |
| 故障清除五处拿锁之后 | **0** | — |

- 探针漏了 `ISublotBoxCountReader`（MES 箱数），读代码补上：引擎 3175 行与 `SlotCapacityCriterion` 都在锁内调它，同样没设超时（读到的）。
- 故障清除那一行的「0」核过不是标记没打上：`step1/probe-recovery-marks.txt` 里标记打上 145 次（`recovery` 119、`recovery-block` 26），其间零记录。
- `HttpClient` 监听零记录：测试里 RIoT 与 MES 全是替身，生产适配器没被跑到，所以这一层的「零」不说明任何事，结论靠端口层记录。
- 文件锁：`src` 里没有 `Mutex`、`FileShare.None`、`WaitOne`（读到的，grep，不是探针）。SQLite 本身的 I/O 没纳入。

## 红证据（修前）

| 文件 | 用例 | 修前结果（读到的） |
| --- | --- | --- |
| `red-pre-fix-71e62d85.txt` | `GateHeldOnboardSendTests.AVehicleWhoseSocketStopsReadingDoesNotHoldTheGateForTheRestOfTheFleet` | `the round was still stuck on one vehicle's socket after 41.1 s, holding the gate; the other vehicle's clearance, which waited 20 s for it, got [FAULT_RECOVERY_RUNTIME_BUSY]` |
| `red-deaf-peer-pre-fix.txt` | `OnboardPowerLossReconnectTests.AVehicleThatKeepsTalkingButStopsReadingIsLetGoAndReconnects` | 30 秒里车一直在发心跳（6446 字节），最长一次推送 29829 ms，旧代次一直没离开路由表 |

第一条：真实回环 TCP，车那一端只连不读、内核缓冲区先塞满；引擎像工人那样整轮持锁，第一次车载端发送就停在 socket 上。另一台车（没有故障）的人工清除等锁 20 秒，拿到锁应得 `FAULT_RECOVERY_FAULT_NOT_IN_EFFECT`，拿不到才是 `FAULT_RECOVERY_RUNTIME_BUSY`。它在纯净的 `71e62d85` 加这一个测试文件上跑。

第二条是第一步里「推的」那一格，这里实测了：静默窗口只挂在读上，对端还在发心跳时，接收循环停在排在卡住写后面的 HeartbeatAck 上，永远走不到读超时。这份输出是该用例较早的版本（`3698459a`，单格 `[Fact]`，没有「先等到一次心跳」的前提）。现行版本的修前红由下面的变异 M0 给出。

## 修复之后（green/）

- `01-journey-row-lost-update.txt`：`JourneyRowLostUpdateTests` 7 条全绿，含 cs#357 点名的 `ABlockCommittedBeforeTheGateLegIsCreatedStopsTheOrder` 与 `ABlockCommittedWhileTheEngineAdvancesACommittedLoadStaysBlocked`。发送位置本票一处没动，仍在一次受守护的保存之后（读到的）。
- `02-cs342-model-1000.*`：1000 组，masterSeed=342，无违规行；录入请求 1000/1000 到车；ack 冲突 0；`ORDER_HANG` 13 轮。与 `evidence/cs276/cs342-model-1000.report.txt` 逐项相同。**模型对本票说明不了多少**：它不经过 `OnboardTcpServer` 与真实的 `OnboardPeerConnection`（见 `evidence/cs276/README.md` 末节），全绿只说明本改动没碰到它覆盖的路径。

- `03-full-suite-release-de9a33f4.txt`：仓库规定的全量命令（`dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release`），本机在 `de9a33f4` 上跑：2868 条全过，退出码 0（含出站 schema 门禁）。比第一步探针那次的 2855 多 13 条，正是本票新增的用例数。

## 反向验证（reverse/）

`reverse/mutate.ps1` 每次注入一处，必须恰好命中一处，否则停；`--no-incremental` 重建；跑本票与相关的六个类（133 条）；用备份还原并核对文件无残留。`M*.record.md` 是 diff、退出码与红名单，同名 `.txt` 是测试输出。基线 `8456b858`。**每一格的预期是跑之前写下的**，下表对照。

| 变异 | 改了什么 | 预期 | 实际红（读到的） |
| --- | --- | --- | --- |
| M0 | 整个上限拿掉（`SendAsync` 直接等写） | 两条红证据、卡住的写、只听不读两格、文本护栏 | 恰好这 5 条 |
| M1 | 超时后不关流，只抛异常 | 卡住的写那条；只听不读可能仍绿 | 只有 `AStuckWriteAndTheOneQueuedBehindIt…`，红在「a send after the timeout took 513 ms to fail」 |
| M2 | 监听器不传 `OnboardTransport:WriteTimeout` | 只有 1 秒那一格 | 只有它，红在「配的写超时是 1.0 秒，旧连接却在 t=5.1s 才被放掉」 |
| M3 | 去掉启动校验 | 两条校验用例 | 恰好 0 秒与 -1 秒两条 |
| M4 | 放弃停车行程时在锁内读一次 RIoT 急停 | 护栏 a 的 `give-up-stopped` | 只有它，`CallsUnderGate` 为 `["emergency read"]` |
| M5 | 放弃停车行程时把收尾快照挪进锁里发 | 护栏 a 的 `give-up-stopped` | 只有它，锁内 3 条发送 |

**M1 为什么只有一条红**（推的，依据是 `OnboardTcpServer.HandleClientAsync`）：只听不读的那条里，接收循环自己的 HeartbeatAck 写也会超时、抛出，循环照样结束并 Detach，所以不关流也能放掉连接。关流真正承重的是「没有接收循环在跑」的那一路（引擎直接发、连接已无人读），那一路只有 `AStuckWriteAndTheOneQueuedBehindItEndWithinTheTimeoutAndTheConnectionIsClosed` 守着。

## 护栏各自守什么

- **a**（`NoFaultRecoveryPathCallsRiotOrSendsToAVehicleWhileTheGateIsHeld`，六条路）：人工出口持锁期间零 RIoT、零车载端发送。每条路都断言走到了自己的结局；放弃停车行程那条还断言发送次数大于零（今天 3 条，全在放锁之后），证明发送检测器看得见发送。原有的 `VehicleFaultRecoveryTests.NoRiotCallIsMadeWhileTheGateIsHeld` 未改。
- **b 行为**：`AStuckWriteAndTheOneQueuedBehindItEndWithinTheTimeoutAndTheConnectionIsClosed`（排在卡住写后面那一次正是 HeartbeatAck 的形状）；只听不读两格（默认值与 1 秒配置）；`TheListenerRefusesToStartWithAWriteTimeoutThatIsNotPositive`。
- **b 文本**：`TheConnectionWritesItsStreamInOnePlaceAndOnlyBehindTheWriteTimeout`。按文本数，看不见换名字的写法，是防回归线，不是证明；socket 的其余出口由 `OnboardOutboundFunnelArchitectureTests` 钉着。

## 审查这一轮（review/，head `fde16258` 的两路审查，调度 2026-09-28 并进本票两处）

审查意见：https://github.com/trytoreachpeak0/8005-agv-control-server/pull/372#issuecomment-5863042360 。提交顺序：先红 `963788cc`（只动 tests），修复 `b51c6279`。修复最初提交为 `c4b3b3b1`，推送前只改了提交说明里的一个计数，代码树完全相同；本节文件名与变异记录里的 `c4b3b3b1` 指的就是这棵树。

### 改了什么（读到的）

- **必修 1，车载端连接不可用时这台车本轮让开。**新异常类型 `OnboardConnectionUnavailableException`（继承 `IOException`）：没连上、代次不对、写超时、连接已关（含 socket 错误与流已关的 `ObjectDisposedException`）都以它结束。引擎推进某台车时遇到它，撤回这台车没保存的改动（与 cs#357 的让开同一件事），看板照 cs#331 的判定写 `JOURNEY_ADVANCE_FAILED`，轮次接着推进下一台（事件 2193）。别的失败仍整轮抛：RIoT 连不上（`HttpRequestException` 里包着 `SocketException`）与崩溃注入（普通 `IOException`）都不在此列，这是新类型收窄的原因。
- **让开时不清空变更跟踪。**`NameFailedAdvanceAsync` 清空再重读，是因为随后整轮就抛了；让开时轮次还要继续，清空会让排在后面的车被循环开头「不在跟踪里就跳过」挡掉。所以只重读、只存这一行；别的车还留着没保存的改动时这一轮不写码，下一轮再写。
- **必修 2，MES。**需求目录与箱数两个 `HttpClient` 设 `Timeout`（`MesIngest:timeoutSeconds`，默认 10 s，在组装时读一次、校验一次）。引擎读箱数、派车读目录、派车判据读箱数三处都把「调用方令牌没取消的取消」并入读失败（`MesIngestReads.IsFailedRead`）。审查点名的是引擎那一处；另外两处机理相同，一并改。
- **建议。**写超时 (0, 10 s] 之外拒绝启动；超时消息带车号与会话代次（连接挂进路由表时写上）。

### 测试的认法随契约改（读到的）

改之前，模拟车载端断线的替身抛普通 `IOException`，注释写着「与真实 `OnboardPeer` 的失败点相同」。改之后这句不再成立：唯一的实现 `OnboardPeer` 一切失败都抛新类型，引擎对它让开。替身不改，这些用例就会继续绿，守的却是一条生产里已经不存在的路径。

- 9 处断线替身改抛产品的新类型（`ArrivalPublishInterruptedThenReconnectedTests`、`Batch7CargoHoldingTests`、`JourneyClosureSnapshotTests`、`ReconnectModel`、`ReconnectModelRegressionTests`、`SlotConfigurationActivationEndpointsTests`）。`ArrivalPublishInterruptedThenReconnectedTests` 里那条多形状理论（第 593 行 `wrapped-io`）保持普通 `IOException`：它测的是整轮抛那条路径上 `IsTransportFailure` 的判定，不代表真实断线。
- 「这一轮必须抛」共 12 处，改成「这一轮照常走完，且断线确实触发过」（`RoundCutAsync` 与计数）。原来的抛异常同时证明了断线注入生效；拿掉之后必须补上这条，否则没生效的注入看起来也是绿的。**每条用例后面的断言一字未改**，全部照样通过：看板命名、开始时间保持、重连后补发，在让开路径下都成立。
- 精确类型断言 4 处（`OnboardHandshakePushGateTests`、`MultiVehicleExecutionTests`）改成断言新类型，钉住新契约。
- cs#342 模型：把「这一轮有一次发送被拒」也认作这台车推进失败，机会计数（`WaitOnPersonChances`）按新行为计。
- 车队夹具的车载端替身改为转给一个什么都没挂的真实 `OnboardPeer`，抛的是产品自己的异常，不是替身挑的类型。

### 修前红（`review/red-pre-fix-963788cc.txt`，`963788cc` 的测试加修前产品代码，读到的）

| 用例 | 修前 |
| --- | --- |
| 多车让开两格（`ready`、`not-ready-on-own-order`） | 整轮抛 `IOException : No recovered Onboard peer is connected for '老厂前线新多仓位1'` |
| 引擎读箱数挂住 | `TaskCanceledException : ... HttpClient.Timeout of 0.5 seconds elapsing` 冒出整轮 |
| 派车读目录挂住 | 同上 |
| 派车判据读箱数挂住 | 这一轮没有一台车完成判定：超时把每台车都踢出了派车轮次 |
| 写超时上限 10.001 s、60 天 | 照常启动 |
| 只听不读两格 | 超时消息里没有车号 |
| 排队写 | 排队那一次以非 `IOException` 结束 |

写超时上限 0 s、-1 s 两格修前就绿（下限校验上一轮已有）。

### 两条会假红的用例（审查必修 3）

- **排队写。**判据改成：两次都是 `IOException`，至少一次是写超时，之后那一次的失败里没有超时字样。哪个计时器先到点属于调度，新判据对两种先后都成立，这是按机理修的。`review/repeat-15-c4b3b3b1.txt` 连跑 15 遍全绿，只是旁证：审查实测 14 轮红 1 次，按这个比率连绿 15 遍仍有约三分之一的概率是运气。
- **只听不读 1000 ms 格。**前提门槛改为配置值的八成。判据改成事件式：推送以写超时失败，消息里是**配置的**秒数与车号；服务端记了 1003、没有记 1005（放掉它的是写超时，不是静默窗口）。墙钟只剩挂死保护。

### 反向验证（`review/reverse/`，基线 `c4b3b3b1`，读到的）

每格的预期都是跑之前写下的。同跑 11 个类、305 条。

| 变异 | 改了什么 | 实际红 |
| --- | --- | --- |
| M0 | 整个写上限拿掉 | 5 条：两条红证据、排队写、只听不读两格、文本护栏，与上一轮相同 |
| M1 | 超时后不关流 | 只有排队写，红在「之后那一次又等满了超时」（事件式判据） |
| M2 | 监听器不传配置 | 只有 1000 ms 格，红在消息里找不到「1 s 内没写完」 |
| M3 | 去掉启动校验 | 恰好上限用例四格 |
| M4 | 放弃停车行程时锁内读 RIoT | 只有护栏 a 的 `give-up-stopped` |
| M5 | 收尾快照挪进锁里发 | 只有护栏 a 的 `give-up-stopped` |
| M6（护栏 b，审查建议） | 连接类里另加一处直接写流 | 只有文本护栏，红在「写流恰好一处」 |
| M7（调度点名） | 让开改回整轮抛 | 24 条：多车让开两格，加上 22 条断线用例（cs#331 那一类 18、货物保持 3、模型回归 1） |
| M8 | MES 判据里取反去掉 | 4 条：3 条 MES 用例，加上既有的单车预算用例（见下） |
| M9 | MES 判据只认 `TimeoutException` | 恰好 3 条 MES 用例 |

- **M7 多出来的 22 条是预期内的。**它们在这一轮被改成断言「断线时整轮照常走完」，所以现在也各自守着让开。模型回归里挂起码那条没红，也符合预期：模型把整轮抛与发送被拒都认作推进失败，两种行为下判的是同一件事。
- **M8 多出的那一条要解释。**这个变异同时做了两件事：让 HTTP 超时不再算读失败，也让派车单车预算到期的取消被当成读失败。多红的 `WhatAVehicleCutOffMidChainHadStagedIsNotSavedWithTheNextVehicle` 是后一半造成的。它说明判据里「令牌没取消」那一半被既有的预算用例守着。M9 只拿掉前一半，恰好红 3 条。

### 修后（review/）

- `full-suite-release-c4b3b3b1.txt`：仓库规定的全量命令，Release，2875 条全过，退出码 0（含出站 schema 门禁）；比上一轮 2868 多 7 条，正是这一轮新增的用例数（多车让开 2、MES 3、上限新增 2 格）
- `cs342-model-1000.*`：1000 组，masterSeed=342，无违规；录入请求 1000/1000 到车；ack 冲突 0；带着等人码而这一轮推进失败 13 次，全是 `ORDER_HANG`。除耗时（这次是 Release 构建）外与 `green/02-cs342-model-1000.report.txt` 逐项相同：模型改了认「推进失败」的方式之后，这条不变量出事的机会一次没少

### 合并 cs#367 之后（`review/merged-1658a972/`，读到的）

推送之前 `fp/v2-impl` 前移到 `d81c78ad`（cs#367，PR #371），合进本分支为 `1658a972`，无冲突，全仓没有冲突标记。cs#367 没有带进新的车载端断线替身；它对 `VehicleFaultRecoveryService` 的改动落在拿锁之前的读取里，只读本服务端的表，护栏 a 的六条路照旧。

- `full-suite-release.txt`：全量 Release 2897 条全过，退出码 0。比合并前的 2875 多 22 条，是 cs#367 带进来的用例。
- `cs342-model-1000.*`：1000 组无违规，除耗时外与合并前逐项相同（机会 13 次，全是 `ORDER_HANG`）。
- `M7.*`：把让开改回整轮抛，仍红同样的 24 条，多车让开 L1 两格在内。

## 增量复核这一轮（recheck/，head `10e476c5` 的两路复核）

复核意见：https://github.com/trytoreachpeak0/8005-agv-control-server/pull/372#issuecomment-5864809364 。两路都判可合、没有必修，合入前补四件。提交：先红 `76cace41`（只动 tests），修复 `3aa93de0`。

### 补了什么（读到的）

1. **钉住异常类型。**排队写那条三次失败都断言**恰好**是 `OnboardConnectionUnavailableException`；只听不读那条断言推送失败记录里的类型名。它是引擎让开的唯一依据。
2. **`MesIngest:timeoutSeconds` 加上限 15 s**（`MesIngestReads.MaxTimeout`），启动时校验。15 s 是故障清除 30 s 等锁的一半。补了 0、-1、NaN、15.001 s、60 天被拒，未配、0.5 s、15 s 照收。
3. **让开时的撤回没有用例走到**：选了写进剩余风险。`YieldToUnavailableConnectionAsync` 的注释写明，这一条靠「先存后发」撑着，与 cs#357 的「冲突即不发」是同一个前提。
4. **注释写明推迟有上限**：让开的车自己的急停确认会推迟，上限靠约 6 s 的静默窗口（`NameSilentOnboardSessionAsync` 接手）。
- `JourneyRuntimeEngine.cs` 这一轮只改了注释：去掉 `///` 行之后，新增代码 0 行。
- 低项四条都写进 PR 的剩余风险，没有改运行时代码。

### 修前红（`recheck/red-pre-fix-10e476c5.txt`，`76cace41` 的测试加 `10e476c5` 的产品代码）

MES 超时 15.001 s 与 60 天两格红（修前只拒 0、负数、NaN），其余绿。收紧后的类型断言在 `10e476c5` 上本来就绿：它们钉的是现有的正确行为，判别力由下面的 M10、M11 证明。

### 反向验证（`recheck/reverse/`，基线 `3aa93de0`，同跑 313 条）

| 变异 | 改了什么 | 预期 | 实际红 |
| --- | --- | --- | --- |
| M10（复核 X2） | 写超时改抛普通 `IOException` | 排队写、只听不读两格 | 恰好这 3 条 |
| M11（复核 X3） | 「连接已关」那一处漏出 `ObjectDisposedException` | 排队写 | 排队写（`Actual: ObjectDisposedException`），**另多 1 条，见下** |
| M12 | 去掉 MES 超时上限 | 15.001 s、60 天两格 | 这 2 格，**另多 1 条，见下** |

**多出的两条不是变异造成的。**分别是 `MultiVehicleExecutionTests.WhatAVehicleCutOffMidChainHadStagedIsNotSavedWithTheNextVehicle`（M11 那一轮）与 `WhenAVehicleExhaustsItsBudgetTheHookIsStillCalledOnceWithOnlyTheVehiclesThatFinished`（M12 那一轮），红法相同：预期只有第 1 台车被派车预算切断，第 2 台也没在预算内完成（其中一条跑了 40 s）。

- **机理排除：**M11 只改真实连接类，车队夹具不经过真实连接；M12 只改 MES 配置校验，车队测试不读这个配置。本票对车队夹具的改动，在没指定断线车、没设转接委托时，与原来的静默替身、原来的替身逐字等价。
- **旁证：**这两条在前面三次全量与 M0～M9 的每一轮里都绿；`recheck/budget-tests-repeat-10.txt` 在干净的 `3aa93de0` 上连跑 10 遍全绿。
- **归因（推的）：**这两条用的是真实计时器的派车预算，变异脚本在 BelowNormal 优先级下与本机其他会话抢 CPU 时，第 2 台车也会超时。这是既有的计时敏感，写进剩余风险。

### 关于 `c4b3b3b1`（复核低项）

`c4b3b3b1` 没有推到远端，远端核不了它的树。`review/reverse/` 那一轮被变异的 5 个产品文件，基线版本与远端 `b51c6279` 里的逐一相同；两者只差提交说明里的一个计数。

## CI 真装置宽清单（照 cs#362，读到的）

run `36382992051`（`l2.yml`，`rig=real`，`mode=consecutive`，`batch_id=cs334`），success，9/9 PASS。

- **三端提交**：从场景那一步读，是 control-server `10e476c54fefe0cdc3460b6fae6504e7b9be8487`、8005-agv-onboard-hmi `4c2d2dc14656f80e812f37128a7964c2c310217a`、slots-simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。
- **逐场景耗时**：normal-load 80 s、load-door-closed-empty-reopens 78 s、compensate-then-reconnect 45 s、restart-while-waiting-operator 68 s、cancellation-authorization-lost 45 s、durable-ack-lost 70 / 69 / 69 s、mixed-side-one-stop 226 s。
- **证据包**：`real-rig-evidence` 已上传（约 2.3 MB）。9 份 `assertions.json` 全部 verdict=PASS，每份 7～12 条判据全过。
- **停止条件**：`RIG_*`／`NOT_STARTED_*` 在日志里命中 9 行，全是源码回显（字面 `^[[36;1m` 前缀，按字节看过）。
- **与最终 head 的关系**：之后的 src 改动只有两处：
  - `MesIngestReads` 的启动校验加上限；
  - `JourneyRuntimeEngine` 的注释（新增代码 0 行）。

  这两处都不碰运行时路径，调度判这次运行仍然有效。启动校验有没有生效，由最终一轮合成 L2 覆盖：每个场景都先起服务端。

