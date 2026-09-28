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

- `03-full-suite-release-de9a33f4.txt`：仓库规定的全量命令（`dotnet test .	ests\ControlServer.Tests\ControlServer.Tests.csproj -c Release`），本机在 `de9a33f4` 上跑：2868 条全过，退出码 0（含出站 schema 门禁）。比第一步探针那次的 2855 多 13 条，正是本票新增的用例数。

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
