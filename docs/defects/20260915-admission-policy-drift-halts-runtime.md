# 缺陷：共享地图上的区号站点一变，运行时每轮抛异常，在途旅程一起停摆（v2 线）

Status: fixed，未上线（修复分支 `fix/v2-admission-policy-drift`，基于 `fp/v2-impl` `d39983d7`；2026-09-18 merge 了
`fp/v2-impl` `6ea64585`）
Found by: 从 `ControlServer_MVP` 线上的同一缺陷移植而来（分支 `fix/admission-policy-drift`，PR #61，那边的缺陷记录与本文件同名）；
v2 上的红测试 `AStationAddedToTheSharedMapDoesNotStrandCargoAlreadyBoundForTheGate`（提交 `0833e2b8`，只含测试）。
v2 服务端没有上过现场，L2 里也没有在 v2 上观测到。
Product at discovery: 服务端 `fp/v2-impl` `d39983d7`。写法与 MVP 相同，自 `9c0d5309`（2026-08-26，
`feat: run durable production journeys`）起就在，拆线时一起带到了 v2。

---

## 现象（推断，未在 v2 上出现）

map 25 是 RIoT 上的共享地图。只要有人在上面新增、删除或改名一个区号命名的站点（比如加一个 `N1-4`），
v2 服务端就会：

- 每一轮记一条 EventId 2002 `Journey runtime iteration failed closed; no stage is inferred from memory.`，别的什么都不做；
- 已经装好货、正开往关卡的车到了关卡等不到卸货指令，停在 `AwaitingGateArrival`；其他阶段的旅程同样不推进；
- 车队里空闲的车也不接新需求；
- 一直持续到有人把 `JourneyRuntime:admissionPolicyVersion` 调高并重启服务。

MVP 那份记录里还有「充电行程不再推进」一条。v2 线上没有自动充电的运行时（MVP 的 `AdvanceAutoChargingAsync`
不在这条线上），这一条在 v2 上不存在。

红测试的输出：

```
Assert.Equal() Failure: Values differ
Expected: AwaitingUnloadResult
Actual:   AwaitingGateArrival
```

## 根因

与 MVP 相同。`JourneyRuntimeEngine.ExecuteOnceAsync` 每一轮读 RIoT 实时地图，把所有区号命名的站点挑出来，当作
准入策略（站点 × `WIRE_TO_GATE`）交给 `WireToGateStore.ApplyAdmissionPolicyAsync`，版本号取自配置。存储层发现
版本号相同而内容哈希不同，就抛 `BusinessIdentityConflictException`（`WireToGateStore.cs` 第 949 行，
`Admission policy version is already bound to different content or deployment identity.`）。这个保护本身是对的：
ADR-cross-0051 不允许不升版本、不留审计就改准入关系。

问题在位置。这次调用（修复前 `JourneyRuntimeEngine.cs` 第 131 行）在地图读取的 `try/catch` 之外，又排在
`AdvanceAsync` 与 `DiscoverAndAcceptAsync` 之前，异常一路冒到 `JourneyRuntimeWorker`，被当成整轮失败记下。于是一次
与本服务无关的地图编辑，变成了对整个运行时的急停。

ADR-cross-0050 与 0051 的要求正相反：准入配置变化只影响尚未越过承诺点的新操作，不中断已承诺的操作；
0050 还专门写了「配置删除不是紧急停止机制」。

## 修法

方向与 MVP 一致：接住异常，在途的照走，新活不接。落位按 v2 的结构调整了两处。

`ExecuteOnceAsync` 在本地接住 `ApplyAdmissionPolicyAsync` 的 `BusinessIdentityConflictException`：

1. **库里的准入关系不动。**已绑定的版本继续有效，实时地图不被当成一次导入。
2. **在途的照常推进。**本轮照常进入 `AdvanceAsync`。到站匹配子批时的站点准入检查（`FindMatchingSublotAsync`
   里的 `IsTaskTypeAllowedAsync`）按库里已绑定的版本判；建关卡段订单前的闸门（`GateLegAsync`）看的是地图目录与
   RouteCost，不看准入策略，照常走。
3. **新活不接。**v2 接新需求只有一条路：每台空闲车在 `DispatchForVehicleAsync` 里让每个候选过准入链
   `DispatchAdmissionChain`。新增 `AdmissionPolicyDriftCriterion`，它读本轮事实 `DispatchRoundFacts.AdmissionPolicyDrifted`，
   为真时返回原因码 `ADMISSION_POLICY_DRIFT`，由引擎写进 `JourneyBacklog`。一轮里每台车读的是同一份本轮事实，所以
   「一台车的旅程照常推进、另一台空闲车被拒」发生在同一轮里。v2 的旅程一车一单，也没有 MVP 那种「在途旅程到站时
   再追加需求」的入口，所以不需要第二处拦截。
4. **单独记一条日志。**EventId 2116，Warning，写明版本号、地图号，以及实时地图比已绑定的集合多了哪些、少了哪些站点。

与 MVP 不同的两处：

- **拦截放在准入链上，而不是在引擎里加一个字段、到评分处特判。**MVP 没有准入链，候选评估是引擎里的一个大方法。
  v2 的 `DispatchAdmissionCriteria` 是准入规则唯一的装配点，注释里写明新规则就是「一个文件加一行注册」。放进链里，
  主机的依赖注入注册与测试用的 `Default` 清单都自动包含它。它排在 Order 75：`PackageCapacityCriterion`（70）之后，
  `VehicleDynamicFactsCriterion`（80）与 `StationTaskTypeAdmissionCriterion`（90）之前。排在 90 之前，是因为 90 查的
  正是已经对不上的那份策略；排在 80 之前，是因为漂移要等人去改地图或版本号才会消失，比车辆忙闲这类一会儿就变的原因
  更值得让看 backlog 的人先看到。与 MVP 的优先顺序一致。
- **EventId 用 2116，不是 MVP 上的 2111。**v2 上 2110–2113 都已经有主，2111 是 `CatalogAvailabilityAccess` 的
  `LogDegraded`。本修复最初用的是 2108（引擎自己这段编号 2101–2107 的下一个空位），但 2026-09-18 merge 主线时
  2108 已被 `LogStationDeadlineEndedStop` 占用，于是改用 2101–2120 里仍空着的第一个号 2116。两条线的日志号因此
  不同，查日志时注意。主线上 2108、2110 各有两处定义，是另一个问题，这里没有动。

版本号回退、`admissionPolicyDeploymentId` 对不上，存储层抛的是同一个异常，这里同样处理：配置指名的策略
与库里绑定的不一致，新活不接，在途的照走。

## 恢复接单

1. 从 EventId 2116 日志读出增减的站点，确认新的站点集合是想要的（新增站点会被允许做 `WIRE_TO_GATE`）。
2. 把部署配置里的 `JourneyRuntime:admissionPolicyVersion` 加 1。选项在启动时读取，必须重启服务才生效。
3. 下一轮服务端把新集合绑到新版本，写一行 `AdmissionPolicyAudit`，接单恢复。

修复上线前，同样的第 2 步也是唯一的恢复办法，只是期间在途的车会一直停着。

## 回归测试

都在 `tests/ControlServer.Tests/JourneyRuntimeWorkerAdmissionTests.cs`（主线 #135 把原来的
`JourneyRuntimeWorkerTests` 拆成了多个类，夹具在 `JourneyRuntimeWorkerTestKit.cs`），名字与 MVP 线上对应的测试相同：

- `AStationAddedToTheSharedMapDoesNotStrandCargoAlreadyBoundForTheGate`：开往关卡途中地图多了一个区号站点，
  车到关卡照样转入 `AwaitingUnloadResult`，这一轮不抛异常；同时新到的第二条需求在车空出来后记
  `ADMISSION_POLICY_DRIFT`，不建新的取货订单。在 merge 前的主线（`6ea64585`）上它失败，原文见上面「现象」。
- `AStationAddedToTheSharedMapTakesOnNoNewDemandUntilThePolicyVersionIsRaised`：车空闲时不接新单，
  原因码 `ADMISSION_POLICY_DRIFT`，库里准入关系与审计不变；版本号调高后当轮接单。
- `AStationAddedToTheSharedMapBeforeArrivalLoadsTheJourneysOwnDemandAndTakesOnNoOther`：按 v2 改写。取货途中地图变了，
  同站出现第二条需求；本单照常装货、建关卡段订单、卸货完成；车空出来的下一轮，第二条需求记 `ADMISSION_POLICY_DRIFT`，
  不建新的取货订单。MVP 版测的是「到站时不把第二条装进工作清单」，v2 没有这个入口，测的是同一条规则在 v2 上真正
  会被问到的那一刻。

MVP 线上的第四条 `AStationAddedToTheSharedMapDoesNotStopAChargingRunUnderWay` 没有移植：v2 没有自动充电运行时。

测试夹具里把 `RunToGateUnloadAsync` 的前半段拆成了 `AdvanceToGateArrivalAsync`，好在车开往关卡途中改地图。

## 结构性派车阻断分类表

`StructuralDispatchClassification`（#74 的分类表，决定一个原因码是普通积压还是要报警的结构性阻断）登记了
`ADMISSION_POLICY_DRIFT`，Order 75，类别 `Backlog`。理由与 `CATALOG_PARAMETERS_NOT_APPROVED` 那一行相同：它一次挡住
所有需求，但原因在配置（版本号与实时地图对不上），不在某一条需求本身；REQ-0210 的报警是按任务的。调高版本号即解除。

## 没有做的

- 更彻底的改法是不再每轮从实时地图推导准入策略，改成部署时有意导入，这才是 ADR-cross-0051 设想的「受控导入」。
  那要动部署配置与流程，不在这次修复范围内。
- MVP 的修复顺带改了 `scripts/l2/README.md` 与 `scripts/l2/scenarios/auto-charge-endurance.ps1` 里「改地图只能增」的说法。
  v2 线上没有这两处文字，也没有那条场景，这里不用改。
