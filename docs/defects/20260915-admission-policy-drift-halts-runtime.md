# 缺陷：共享地图上的区号站点一变，运行时每轮抛异常，在途旅程与充电一起停摆

Status: fixed，未上线（修复分支 `fix/admission-policy-drift`，PR 进 `ControlServer_MVP`）
Found by: 代码审读，2026-09-15；红测试 `AStationAddedToTheSharedMapDoesNotStrandCargoAlreadyBoundForTheGate`
（提交 `78241ad1`，只含测试）。没有在现场或 L2 里观测到，生产上是否已经发生过未查。
Product at discovery: 服务端 `ControlServer_MVP` `6a8a688c`。同样的写法自 `9c0d5309`（2026-08-26，
`feat: run durable production journeys`）起就在，`fp/v2-impl` 上也一样。

---

## 现象（推断，未在现场出现）

map 25 是 RIoT 上的共享地图。只要有人在上面新增、删除或改名一个区号命名的站点（比如加一个 `N1-4`），
服务端就会：

- 每一轮（生产 2 秒一轮）记一条 EventId 2002 `Journey runtime iteration failed closed`，别的什么都不做；
- 已经装好货、正开往关卡的车到了关卡等不到卸货指令，停在 `AwaitingGateArrival`；
- 充电行程不再推进，新需求也不接；
- 服务进程照常运行，`/health/ready` 只看车载会话，仍然可能是 `ready`。

一直持续到有人把 `appsettings.Production.json` 里的 `JourneyRuntime:admissionPolicyVersion` 调高并重启服务。

红测试的输出：

```
Assert.Equal() Failure: Values differ
Expected: AwaitingUnloadResult
Actual:   AwaitingGateArrival
```

对调断言顺序后看到的异常：

```
ControlServer.Domain.BusinessIdentityConflictException: Admission policy version is already bound to different content or deployment identity.
   at ControlServer.Infrastructure.Persistence.WireToGateStore.ApplyAdmissionPolicyAsync(...) WireToGateStore.cs:line 895
   at ControlServer.Host.Runtime.JourneyRuntimeEngine.ExecuteOnceAsync(...) JourneyRuntimeEngine.cs:line 122
```

## 根因

`JourneyRuntimeEngine.ExecuteOnceAsync` 每一轮读 RIoT 实时地图，把所有区号命名的站点挑出来，当作准入策略
（站点 × `WIRE_TO_GATE`）交给 `WireToGateStore.ApplyAdmissionPolicyAsync`，版本号取自配置。存储层发现
版本号相同而内容哈希不同，就抛 `BusinessIdentityConflictException`。这个保护本身是对的：ADR-cross-0051
不允许不升版本、不留审计就改准入关系。

问题在位置。这次调用在地图读取的 `try/catch` 之外，又排在 `AdvanceAsync`、`AdvanceAutoChargingAsync`、
`DiscoverAndAcceptAsync` 之前，异常一路冒到 `JourneyRuntimeWorker`，被当成整轮失败记下。于是一次与本服务
无关的地图编辑，变成了对整个运行时的急停。

ADR-cross-0050 与 0051 的要求正相反：准入配置变化只影响尚未越过承诺点的新操作，不中断已承诺的操作；
0050 还专门写了「配置删除不是紧急停止机制」。

`scripts/l2/README.md` 第 145 行原先总结为「改地图只能增，不能换」，这对区号站点并不成立，多一个也会变。
那条经验来自追加充电桩，而充电桩名字不是区号格式，本来就不进准入集合。

## 修法

`ExecuteOnceAsync` 在本地接住 `ApplyAdmissionPolicyAsync` 的 `BusinessIdentityConflictException`：

1. **库里的准入关系不动。**已绑定的版本继续有效，实时地图不被当成一次导入。
2. **在途的照常推进。**本轮照常进入 `AdvanceAsync` 与 `AdvanceAutoChargingAsync`。旅程里已经接下的需求
   照常装货、出车、卸货，站点准入仍按库里已绑定的版本判；充电行程照常推进。
3. **新活不接。**候选评估里本来可以接的需求一律记原因码 `ADMISSION_POLICY_DRIFT` 写进 `JourneyBacklog`，
   包括车空闲时的新旅程，也包括给在途旅程追加需求（到站时装进工作清单、装货阶段再接一单）。写法与
   `CHARGER_STATION_UNRESOLVED` 相同。
4. **单独记一条日志。**EventId 2111，Warning，写明版本号、地图号，以及实时地图比已绑定的集合多了哪些、
   少了哪些站点。

版本号回退、`admissionPolicyDeploymentId` 对不上，存储层抛的是同一个异常，这里同样处理：配置指名的策略
与库里绑定的不一致，新活不接，在途的照走。

## 恢复接单

1. 从 EventId 2111 日志读出增减的站点，确认新的站点集合是想要的（新增站点会被允许做 `WIRE_TO_GATE`）。
2. 把 `appsettings.Production.json` 里的 `JourneyRuntime:admissionPolicyVersion` 加 1。选项在启动时读取，
   必须重启服务才生效。
3. 下一轮服务端把新集合绑到新版本，写一行 `AdmissionPolicyAudit`，接单恢复。

修复上线前，同样的第 2 步也是唯一的恢复办法，只是期间在途的车会一直停着。

## 回归测试

都在 `tests/ControlServer.Tests/JourneyRuntimeWorkerTests.cs`：

- `AStationAddedToTheSharedMapDoesNotStrandCargoAlreadyBoundForTheGate`：开往关卡途中地图多了一个区号站点，
  车到关卡照样转入 `AwaitingUnloadResult`，这一轮不抛异常。
- `AStationAddedToTheSharedMapTakesOnNoNewDemandUntilThePolicyVersionIsRaised`：车空闲时不接新单，
  原因码 `ADMISSION_POLICY_DRIFT`，库里准入关系与审计不变；版本号调高后当轮接单。
- `AStationAddedToTheSharedMapBeforeArrivalLoadsTheJourneysOwnDemandAndTakesOnNoOther`：取货途中地图变了，
  同站新出现的第二条需求不进工作清单，第一条照常装货、到关卡、卸货完成。
- `AStationAddedToTheSharedMapDoesNotStopAChargingRunUnderWay`：去充电桩途中地图变了，到桩后照常进入 `Charging`。

## 没有做的

更彻底的改法是不再每轮从实时地图推导准入策略，改成部署时有意导入，这才是 ADR-cross-0051 设想的「受控导入」。
那要动生产配置与部署流程，不在这次修复范围内。
