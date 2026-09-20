# 路线证据判据的三次对照（control-server#203，条目 2）

票面条目 2 要把 `g3-reversed-direction-journey` 的 `G3-11-07` 从「路线证据非空」收紧成「等于按计划
方向独立重算出来的那个 id，且把两端互换重算会得到另一个」。这里记的是**这次收紧本身的判别力**——
改的是判据，所以「改完跑绿了」什么都不说明。

## 非空为什么几乎没有判别力

`JourneyRuntimes.RouteEvidenceId` 是这趟旅程被冻结的身份，幂等重放按它比对
（`MapStationResolver.BuildRouteEvidenceId`：图号、图内容指纹、起点站 id 与站名、终点站 id 与站名、
AREA、EQP，八项按序拼起来做 SHA-256）。**把两端喂反、漏掉一项、换掉拼法，存进去的仍然是一个非空的
`MAPCAT-…`。** 而这趟场景恰恰就是 `NEVER_SWAP_ORIGIN_AND_DESTINATION` 那条产品断言的现场。

## 三次运行

对照跑在合成对端的同路径场景 `staging-to-wire-reversed-journey` 上，新判据是它的 `L2-S2W-08`。
选它不是图省事：真车载端那条要排交互式桌面，而**重算写错会让判据必然红，那种红在真装置上最容易被
读成「产品坏了」**——所以先在这里把它跑绿再说。

| 目录 | 判据 | 服务端 | `L2-S2W-08` | 说明 |
| --- | --- | --- | --- | --- |
| `cs203-s2w08-clean-green` | 收紧后 | 干净 | PASS | 判据不是必然红，且重算与服务端在真实运行数据上算出同一个值 |
| `cs203-s2w08-red-swapped-hash` | 收紧后 | **缺陷** | **FAIL** | 8 条判据里**只有这一条**红 |
| `cs203-s2w08-old-check-still-green` | **收紧前**（只判非空） | **缺陷** | PASS | 这一格是本节的论据：旧写法看不见那个缺陷，8 条全绿 |

干净那次的实际值：服务端存的是 `MAPCAT-70d1cd847c60826781ceb6f6c4a8af0949f65dda699af83c12909a666efad7c0`，
pwsh 独立重算出同一个值，把两端互换重算是
`MAPCAT-938605c023d7d6618ef8ef781093e36de5ea5a2051ab9d67f560d04fe743b028`。

## 注入的是哪一种缺陷

只把**喂给哈希的那一对端点**反过来，别的一行不动：

```csharp
// JourneyPlanBuilder.ResolveRoute
MapStationResolver.BuildRouteEvidenceId(map, new RouteEndpoints {
    Origin = endpoints.Destination, Destination = endpoints.Origin }, area, eqp),
```

这样 `JourneyRuntimes` 里记下的 `PickupStationId`／`GateStationId` **仍然是对的**，计划、订单、清单、
准入全都照常——**只有这趟被冻结成的那个 id 是按反方向算的**。这正是旧判据看不见、而新判据要抓的形状；
也是为什么红点只落在一条上，而不是把整趟都打红。

注入与还原都按字节做，两个文件跑完核对过字节级还原，没有用 `git checkout --`（同文件里有本票未提交的
改动）。

## 重算凭什么与服务端一致

重算在 `scripts/l2/L2RouteEvidence.psm1`，是第二份实现，所以它自己也要有人管：

- 站点表到指纹那一半，钉在 `HttpRiotMovementGatewayTests`
  （`StrictMapCatalogIsCanonicalAndObservedAfterSdkReadCompletes`，含中文站名，顺带验了 UTF-8）；
- 指纹到 id 那一半，钉在 `JourneyPlanCharacterizationTests.TheRouteEvidenceIdOfAWireToGateCandidateIsPinned`。

改了服务端任一边的拼法，它自己那条测试先红，那就是「另一份实现要跟着动」的通知——**所以那两条测试各加了
一句备注写明这件事**，否则看到红的人会直接更新期望值，而那份 pwsh 复刻既没人构建也没有 C# 测试读它。

> 本来是新写一条 L1 测试来钉指纹到 id 那一半的，写完发现 `JourneyPlanCharacterizationTests` 早就按字节钉死了
> 同一个函数的同一套拼法。**同一个拼法改动会让两条一起红，多出来的那条不提供额外判别力**，反而把「要同步改
> pwsh」这个提示分散到两处，所以删掉了，改用已有那一对。顺带也不用动切片账本
> （新测试带切片 trait 会让 `MapStationResolverTests` 从「整类在切片之外」的名单里掉出来）。

`scripts/l2/Test-L2RouteEvidence.ps1` 用同样两对值断言 pwsh 这一份，九条用例。对模块做了八种变异，
每一种都被抓到，且红的是该红的那几条：漏掉 EQP、两端写反、分隔符、前缀、起点哈希两遍、站点不排序、
指纹漏站名、十六进制大写。

**变异测试顺带抓出一条自己的假绿，值得记下来。**「每个输入都进哈希」那条原本把八次扰动的结果与**钉死值**
比。漏掉最后一个输入时，八次扰动的结果仍然两两不同、也都不等于钉死值，于是那一条照样绿——它其实只是在
重复钉死对那两条。改成与**这份实现自己算出的基准**比之后，漏掉 EQP 才真的让它红。指纹那条有同样的毛病，
一起改了。

## 还没做的

真车载端那条 `G3-11-07` 是同一件事的同一份重算，但它要排交互式桌面，等真装置时段。
