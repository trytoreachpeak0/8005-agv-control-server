# 缺陷：车载端配置声明比对的是车上没在跑的那一版绑定，并列的车型还会被跨线比版本号

Status: fixed
Owner repository: `8005-agv-control-server`（`src/ControlServer.Infrastructure/Persistence/SlotConfigurationAuthorityStore.cs`、`src/ControlServer.Infrastructure/Persistence/AreaAssignmentStores.cs`、新增 `src/ControlServer.Infrastructure/Persistence/VehicleSlotModelResolver.cs`）
Found by: 代码审查，不是跑出来的——[PR #93](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/93) 与 [PR #94](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/94) 的审查意见
Product at discovery: `fp/v2-impl@628f0693`
Fixed in: 见提交记录（本单与修复同一提交）

**`VerifyVehicleDeclarationAsync` 目前在 `src/` 与 `tools/` 里没有调用方，所以这三条都还没有跑到过运行路径上。**
接线一旦接上——协议 v2 的车载端握手报配置声明、服务端核验——第一条就会立刻发作在每一次回滚之后。

## 现象

三条，同一个根源。

### 一、回滚之后，一台接线完全正确的车被判成不一致

回滚不是「把历史改回去」，是**取旧版内容在当下发起一次新的激活**（`GovernedActivationStore.RollbackAsync`）：
产出一版新的冻结快照（内容与旧版逐字节相同）与一条新的激活记录，被回滚到的那一版一个字节不动。
车补报成功后，`ActiveSlotConfigurations` 记的是这次回滚的 `ConfigurationVersion` 与 `SnapshotId`。

而核验拿的是「这个车型下 `Version` 最大的那一批绑定行」。回滚之后这两者指的不是同一份东西：

| | 内容 |
| --- | --- |
| 车上此刻跑着的（生效配置的快照） | v1，脉冲复位 500 ms |
| 绑定表里 `Version` 最大的那一版 | v2，脉冲复位 800 ms |

于是一台照 v1 接好线的车，每个仓位的 `pulseResetMilliseconds` 都报不符；而一台仍按被回滚掉的 v2 接线的车——那台才是真该被拦下的——反倒判成一致。

「激活之后又发布了一版绑定，但还没有再激活」是同一件事的另一种走法。发布绑定是硬件相关变更，它让这台车重新待核验（REQ-0263），
但没有改变车上跑着的那一份：车还是生效那一版的接线。按绑定行比，结论同样是反的。

### 二、`CreatedAt` 并列时跨车型比版本号，比的是两条互不相干的号码线

车型定不下来时，两处都退到「按 `CreatedAt` 倒序，相同再按 `Version` 倒序」取第一行。
但 `Version` 是按（车，车型版本）**各自从 1 编号**的（`SlotConfigurationAuthorityStore.PublishIoBindingsAsync`），
一台车先在车型 A 下绑了两版、又换到车型 B 绑了第一版时，「A 的 2 比 B 的 1 大」不说明 A 比 B 新——它们数的不是同一条线。
PR #94 修掉了「不并列时按版本号挑」这一半，并列时的次级判据仍然是它。

`CreatedAt` 本身也不是写入顺序：它就是调用方传进来的 `occurredAt`。所以并列时既不能比版本号，也不能靠写入先后兜底。

### 三、同一段逻辑有两份近乎逐行相同的副本

`VehicleSlotPositionReader.ResolveModelAsync`（派车读仓位分组）与 `VerifyVehicleDeclarationAsync`（核验声明）各写了一遍
「生效配置的车型，否则最新一版已发布绑定的车型」。两份都带着上面第二条那个错误，而且是各自带着——
`8005-agv-control-server#66` 把这个解析顺序定成了一条规则，一条规则有两份实现，下次只会改对一份。

## 原因

**把「这台车绑的是哪个车型」与「这台车此刻跑着哪一份配置」当成了同一个问题。**

车型只回答「该按哪一版整车模型理解这台车的仓位」，派车读分组要的就是这个。
核验声明要的是另一样东西：车上此刻实际生效的那一份 IO 绑定内容。有生效配置时，那份内容写在
`ActiveSlotConfigurations.SnapshotId` 指的那版冻结快照里；「该车型下版本号最大的绑定行」只是它在从没回滚、
也没有更新发布的那段时间里恰好相等的一个近似。

## 修复

新增 `VehicleSlotModelResolver`，是解析顺序与并列规则的唯一实现处，两个调用方共用：

- `ResolveAsync` —— 车型：生效配置引用的车型 → 该车最新一版已发布 IO 绑定引用的车型 → 未解析。
  `8005-agv-control-server#66` 定的顺序一字未改：不回退到已批准八仓事实的默认车型，也不按仓号区间推断，未解析由调用方 fail-closed。
- 并列规则定为**跨车型并列即未解析**（fail-closed），`ThenByDescending(Version)` 整个去掉。
  没有选另一条路（拿快照 `FrozenAt` 或审计序号做单调序）的理由是：`FrozenAt` 与绑定行的 `CreatedAt` 一样来自调用方传入的
  `occurredAt`，换一列读并没有换来一个真正的写入顺序，只是把同一个猜测挪个地方；而审计表的顺序是另一张表的实现细节，
  让「这台车是什么车型」去依赖它，等于新造一条隐含规则。跨车型并列本来就没有正确答案，只有一个安全答案。
  并列落在同一个车型下时不是歧义（一次批量导入用同一个时间戳发两版绑定就是这样），照常解析到那个车型。
- `ReadActiveContentAsync` —— 车上此刻跑着的那份内容：有生效配置就读它冻结的那版快照的内容，**没有生效配置才**退到该车型下
  最新一版已发布绑定。生效配置在、而快照读不出内容时返回未解析，**不退回绑定行**——退回去就是上面第一条那个错误答案。

`VerifyVehicleDeclarationAsync` 改为按 `ReadActiveContentAsync` 的结果比对。解析不出来时没有权威值可比，声明一律不通过，
每个报上来的仓位记一条 `slot` 不符。

顺带收掉「某车型下最新一版已发布绑定」的三份副本（激活协调器两处、就绪门禁一处）到
`ReadLatestPublishedBindingsAsync`，行为不变：车型版本已经定死，同一条（车，车型）版本线上比版本号是有意义的。

**没有改 `src/ControlServer.Application/AreaAssignmentPorts.cs` 里的任何端口签名**，批次 4 的
[#72](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/72)、
[#74](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/74) 要调的 `IVehicleSlotPositionReader` 原样不动。

## 证据

先写测试、确认为红，再改产品代码。

| 项 | 结果 |
| --- | --- |
| 新增 `SlotConfigurationAuthorityTests.AfterARollbackTheDeclarationIsVerifiedAgainstTheContentTheVehicleWasRolledBackTo` | 修复前红（`Assert.True() Failure / Expected: True / Actual: False`——按 v1 接线的车被判不一致）；修复后绿 |
| 新增 `SlotConfigurationAuthorityTests.ABindingPublishedAfterActivationDoesNotBecomeTheThingTheVehicleIsVerifiedAgainst` | 修复前红（同上）；修复后绿 |
| 新增 `SlotConfigurationAuthorityTests.TwoModelsBoundAtTheSameInstantMakeTheDeclarationUnverifiableRatherThanVerifiedAgainstAGuess` | 修复前红（`Assert.False() Failure / Expected: False / Actual: True`——猜出了一个「通过」）；修复后绿 |
| 新增 `VehicleSlotPositionReaderTests.TwoModelsBoundAtTheSameInstantLeaveTheVehicleUnresolvedRatherThanPickingTheLargerVersion` | 修复前红（`Assert.Null() Failure`，返回了车型 A）；修复后绿 |
| 新增 `VehicleSlotPositionReaderTests.TwoBindingsOfOneModelAtTheSameInstantStillResolveToThatModel` | 前后都绿——护栏，防止 fail-closed 扩大到同车型并列 |
| 全量 `dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release` | 811 passed / 0 failed / 0 skipped |

没有跑 L2，也没有进任何门禁：这次改的是持久化层的读取判据，全在单进程内，L2 覆盖的是跨端时序。
