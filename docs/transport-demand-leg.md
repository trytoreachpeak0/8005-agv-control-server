# 需求身份加「程」：同一子批两程送焊线（control-server#911）

## 一句话

运输需求的身份从「业务键」（`子批|任务类型`）改成「业务键 + 程」。程只对送焊线（`STAGING_TO_WIRE`）有值，取受理时 MES 的 `STEP`（「焊线」「焊线2」……）；其余六类任务的程恒为空串，行为与改动前逐字相同。受理唯一、按身份永久抑制（REQ-0155）、完成事实三处都按新身份判。

## 为什么

mes-ingest#15（2026-11-21 23:45 上生产）给送焊线的取料工序加了「焊线2」：同一个子批先送「焊线」机台，机台做完再送「焊线2」机台，两程的业务键相同。v2 原先按业务键判身份，于是第二程被第一程挡住：

- 第一程送达过，第二程被受理链第 12 道挡成 `TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED`；
- 第一程被取消过，第二程被第 11 道挡成 `TRANSPORT_DEMAND_KEY_SUPPRESSED`；
- 就算放过这两道，第二程送到时完成事实表按业务键做主键，卸货结果会被判成 `BUSINESS_ID_CONTENT_CONFLICT`。

客户反馈是「agv02 收不到送焊线2 的任务」。调查与方案比较在工作区 `8005-scratch/staging-wire2-20261122/FINDINGS.md`、`OPTIONS.md`。

## 用户批准的决定（2026-11-23）

经调度 Coordinator14 转达，用户在调度窗格选定（并在 cs#914 调研对比后确认「回方案乙」）：

1. 方案乙：业务键不动，加「程」字段 `TransportLeg`；受理唯一索引、抑制主键、完成主键都改成「业务键 + 程」。
2. 按程压制：焊线那一程被取消，不影响焊线2 那一程。
3. 批准修订需求词汇表里「STEP 不参与 TransportDemandKey／避免 SUBLOT+STEP」这条定义：只对 `STAGING_TO_WIRE`，按 STEP 分程。
4. 不加新原因码 `DEMAND_STEP_CHANGED_AFTER_ACCEPTANCE`。

## 需求文档的修订稿

需求词汇表在 `8005-agv-program/CONTEXT.md`，本仓库不含它。以下是按第 3 条决定拟的修订文字，供同步到那里；`TransportDemandKey` 本身的定义不变，新增「程」，并改写依赖它判身份的三条。

- **TransportLeg（运输程）**（新增）：同一 TransportDemandKey 下先后出现的不同搬运。只有 `STAGING_TO_WIRE` 分程，程取受理时 LiveMesFieldSet 的 `STEP` 原文（焊线、焊线2），其余任务类型的程为空。程只用来区分身份，不用来判断方向（方向仍由任务类型决定，REQ-0184 不变）。分程的任务类型是代码里的显式清单，新增要开票。
  _Avoid_: 按 STEP 猜方向、对所有任务类型按 STEP 分程。
- **STEP**（改写末句）：原「不参与 TransportDemandKey」改为「不参与 TransportDemandKey；对 `STAGING_TO_WIRE` 决定 TransportLeg」。
- **TransportDemandSuppression**（改写）：原「以 TransportDemandKey 记录的永久禁止业务执行事实」改为「以 TransportDemandKey 与 TransportLeg 记录」；同一子批另一程不受影响，与「另一任务类型不受影响」并列。REQ-0155 末句同样补上「同一 SUBLOT 的 `STAGING_TO_WIRE` 另一程（STEP 不同）属于不同身份，不受原抑制影响」。
- **TransportDemandCompletion**（改写）：完成事实以 TransportDemandKey 与 TransportLeg 为键，阻止同一程重复执行；另一程不受影响。
- **TransportDemandKey** 的 _Avoid_ 列表保留「SUBLOT+STEP」：业务键本身仍不含 STEP，STEP 只进「程」。

## 行为

| 情形 | 结果 |
| --- | --- |
| 焊线那程送达，焊线2 那程出现 | 受理、派车、送达，各有一条完成事实 |
| 焊线那程被取消（抑制），焊线2 那程出现 | 受理、派车（按程抑制） |
| 同一程以新 `DemandId` 再发（MES 重复下发、换代） | 照旧被第 12 道挡（`STEP` 不变） |
| 同一程取消后再现 | 照旧被第 11 道挡 |
| 同一个 `STEP` 走两次（例如返工） | 仍被挡，与改动前相同 |
| 其余六类任务，`STEP` 变了 | 仍是同一件活（不分程） |
| 送焊线的 `STEP` 为空 | 程为空串；MesIngest 在 `STEP` 为空时本来就不让外读，正常走不到 |

已知的残留：如果 MES 在需求没有离开目录的情况下，把同一个 `DemandId` 的 `STEP` 从焊线改成焊线2，第二程用的仍是旧 `DemandId`，会被第 10 道 `DEMAND_ALREADY_ACCEPTED` 挡住。现场目前没有观测到（`Q26113563-4` 两程之间离开过目录，generation 为 2）。用户决定不为它加原因码。

## 迁移

`20261123074049_TransportDemandLeg`：

- `AcceptedDemands`、`TransportDemandSuppressions`、`TransportDemandCompletions` 各加 `TransportLeg`（非空）。
- 回填：受理行只有 `STAGING_TO_WIRE` 取冻结的 `LiveMesFieldsJson` 里的 `Step`（空白为空串）；抑制行与完成行按自己的 `DemandId` 取受理行的程。2026-11-23 只读核对过现场 v2 库：压制表 10655 行、完成表 2771 行全部能按 `DemandId` 找到受理行；受理表没有 `STEP` 为空的行。
- 受理唯一索引改为 `(TransportDemandKey, TransportLeg)`；两张表的主键同样改，SQLite 上为此整表重建（现场规模下秒级）。
- 降级（`Down`）：同一业务键下有不止一程时，最早写下的一行保留原键，其余各行的业务键改成 `{key}#{leg}`，再恢复按业务键唯一。

## 部署与回退

- **部署**：照常停服务、换包、启动，启动时自动迁移。不需要额外停受理；业务键、协议都没变，在途旅程（包括在途的送焊线）不受影响，车载端不用升级。
- **部署后立刻会发生的**：此刻还挂在目录里、因焊线那程而被挡住的焊线2 需求，下一轮就会进入派车判断。11-23 15:15 只读查到的只有 `Q26113563-4` 一条；之前统计的其余 35 个子批已经离开目录，不会再派。
- **约 300 条积压的压制批次**：它们压的是焊线那程（或别的任务类型），新语义下照旧压制，不会被放回。放不放回仍是用户另行决定的事。
- **回退必须连库一起回退**：恢复部署前的数据库副本（`docs/field/control-server-database-copy.md`），再装旧包。不要让旧包直接跑在迁移过的库上：两张表重建后 `TransportLeg` 是没有缺省值的非空列，旧包写抑制或完成会撞非空约束；而且同一业务键下有两程时，旧包按键读一行会抛异常。降级迁移（`Down`）只在测试里验过，现场没有现成的执行工具；若要保留部署后写下的记录而不恢复副本，需另开票做降级步骤。
- **回退的代价**：恢复部署前的库副本，部署之后写下的受理、压制、完成记录全部丢失。其中最要紧的是：部署后才被取消或送达、但此刻仍挂在 MES 目录里的需求，在旧库里没有那条终态记录，回退后会被当成从没办过，可能再派一次车。回退前先在看板上记下部署后终结的需求，回退后逐条核对，必要时人工处理。

## 剩余风险

- **源码守卫的盲区。** `TransportDemandIdentityArchitectureTests` 按正则认按业务键的比较。它认不出实例方法写法 `a.TransportDemandKey.Equals(b)`，也认不出先 `Select` 出业务键、再拿结果 `Contains` 的写法；把键放进别的变量再比、经 SQL 字符串比，同样看不见。今天产品源码里没有这几种写法，审查逐处读过全部引用。
- **直接完成入口没有生产调用方。** `WireToGateStore.CompleteDemandAfterUnloadAsync` 只被测试夹具用来种「已送达」（18 个测试文件），生产完成走车载端卸货结果 `ApplyOperationResultAsync`。两条路径都按程写完成、都有两程用例；删掉前者要改写那 18 个文件，不在本票。
- **释放待改派与另一 `DemandId` 占了新程**：一条需求释放待改派时，同一程若已被另一个 `DemandId` 受理，第 12 道会一直挡它（审查推断，未观测到）。

## 测试与证据

- `TransportDemandLegTests`：两程在受理链第 11、12 道、防饥饿排除、终结写抑制、存储层受理与完成上的行为。
- `TransportDemandLegMigrationTests`：回填只填该填的、其余每个值不变；升级后写第二程、降级改名、再升级。
- `TransportDemandIdentityArchitectureTests`：产品源码里剩下的按业务键比较逐处点名。
- L2 `staging-to-wire-second-leg`：焊线那程送达 → 同一程再发被挡 → 焊线2 那程送达、两条完成事实。
- 变异与 L2 绿红对照：`evidence/cs911/`。
