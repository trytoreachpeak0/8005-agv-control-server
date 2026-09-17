# 批次 4 出口报告（v2 线）：仓位分组与分区归属表

日期：2026-09-17
出口票：[control-server#76](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/76)（批次4（第二版）-12）
服务端：`fp/v2-impl@1ce808a4` 开出的 `fp/v2-b4-12-batch4-exit`，出口提交 `03ec8a8c`（在 `1ce808a4` 上只改了 `l2.yml`）
车载端：`8005-agv-onboard-hmi` 的 `w2g/fp-v2-impl@08bcf7d`（含批次4（第二版）-02 的合并 `12bf8586`）
规格：`8005-agv-program` 仓 `docs/specs/full-product-scope-and-sequence-v2.md`（2026-09-15 批准，第 19 节补记优先于正文）
需求基线：`v1.3.0`（`REQ-0191`、`REQ-0208`、`REQ-0210`、`REQ-0349`～`REQ-0353`）

**批次 4 做的事**：客户 2026-09-14 反映多仓位 AGV 停靠时只有一侧仓门够得着机台边的架子。八个仓位分为前侧（1～4 号，`FRONT`）
与后侧（5～8 号，`REAR`），每个区域号（AREA）的机台只能用其中一侧。批次 4 把开门侧作为必填列并入分区归属表，派车时按需求
AREA 所属的组、组内按编号升序选仓，一条需求不跨组。只在服务端判断，不改协议。

## 结论

**批次 4 没有切片，出口只看 L1 与 L2 两条（规格 8.2、7.4 第 11 条），两条都成立。**门禁不在本批次出口范围内；真装置 L2 跑了一次、PASS。

| 出口（规格 8.2／8.3、#76 验收） | 状态 | 依据 |
| --- | --- | --- |
| L1：两端测试套件全绿，新能力逐项有新增覆盖 | **成立** | 服务端 1093 passed（`03ec8a8c`）；车载端 307 ＋ 118 passed（`08bcf7d`）。逐项对照见第一节 |
| `l2.yml` 批次 4 八行都在，`Runs = 3`、`BatchId = 'batch-4'`，超时有依据 | **成立** | 八行由各功能票加入，无遗漏；本票改为 `Runs = 3`。超时不改，依据见第二节 |
| L2：八个场景在 CI 上各连续三次通过，证据各自独立 | **成立** | CI run `35196671017`（`03ec8a8c`），`mode=consecutive-all`。见第二节 |
| `identity` 含 `batchId = batch-4` 与读回的 `protocolReleaseIdentity` | **成立** | 读回值为 `protocol-v2.0.0@86575456`，`approvalStatus = SUPERSEDING_CANDIDATE`（**未发布的候选**，不是 `v1.0.0`），见第二节 |
| 批次 2、3 既有场景（及已合入的批次 5 场景）同一次 CI 全绿 | 见 PR | 出口 PR 自己那一轮 `l2`（默认模式，清单 26 个场景各一遍，`session-established-while-moving` 三遍）；run 号写在 PR 正文与 #76 评论里，因为报告提交早于那一轮 |
| 第 8.3 节七个场景各有证据目录，负向场景标明 | **成立** | 第二节表格；④、⑥、③b 标为负向 |
| 门禁与真装置 L2 | **真装置跑了，门禁未跑** | 真装置 `real-onboard-normal-load` PASS；`CONTROL_SERVER_G2` 未跑及理由见第三节 |
| 每次 `-EvidenceRoot` 为新目录，红证据保留，失败原因有记录 | **成立** | 本票自己的运行无红；批次 4 期间四次无缺陷单的红补登记在 `docs/defects/20260917-batch-4-red-l2-runs-registered-at-exit.md`，见第四节 |

## 一、L1

### 实测

```
服务端（fp/v2-b4-12-batch4-exit@03ec8a8c，Release）
Passed!  - Failed:     0, Passed:  1093, Skipped:     0, Total:  1093, Duration: 3 m 3 s - ControlServer.Tests.dll (net8.0)

车载端（w2g/fp-v2-impl@08bcf7d，dotnet test .\SQCD_8005AGV.sln -c Release）
Passed!  - Failed:     0, Passed:   307, Skipped:     0, Total:   307, Duration: 8 s - SQCD.Agv.UnitTests.dll (net8.0)
Passed!  - Failed:     0, Passed:   118, Skipped:     0, Total:   118, Duration: 13 s - SQCD.Agv.WireToGateG2Tests.dll (net8.0)
```

两个命令退出码均为 0。

### 新能力逐项覆盖

计数单位是本票 PR 自己的提交里新增的 `[Fact]`／`[Theory]` 方法（不含 InlineData 展开，不含后续 PR 往同一类里加的），不比总数。
测试类都在当前 HEAD 上核对过仍存在。

| 能力（票） | 守它的测试（新增） | 证明什么 |
| --- | --- | --- |
| 分区归属表与读取器（#66，PR #93） | `AreaAssignmentStoreTests`（4）、`VehicleSlotPositionReaderTests`（7）、`StructuralDispatchBlockStoreTests`（3）、`DemandAreaAssignmentFreezeTests`（5）、`Batch4MigrationDisciplineTests`（7）、`SlotConfigurationAuthorityTests`（+1） | 每次导入形成不可改的新版本并带快照与审计；车的分组先取生效配置、再取最近 IO 绑定，解析不出就给「未解析」不猜；阻断表去重；需求只冻结一次；批次 4 只有一个迁移，`LEFT`／`RIGHT` 改名只动 `SlotPosition`，快照与审计逐字节不变；种子为 1～4 `FRONT`、5～8 `REAR` |
| 导入五类错误与预览（#68，PR #102） | `AreaAssignmentImportTests`（15） | 五类错误与表头错误整份拒绝并一次报出全部坏行；`--dry-run` 不写；预览列出开门组会变的在途需求；同一站点两个 AREA 分到相反两组照常通过 |
| 派车链接缝（#69，PR #96） | `DispatchChainSeamTests`（9）、`MultiVehicleExecutionTests`（+5） | 查表步骤的位置与「只记录不拒绝」；四个分组原因码定名；重放比对冻结版本与分组；每轮只读一次归属表与车辆分组；轮末钩子调用一次 |
| 看板两组数据（#70，PR #114） | `DispatchBacklogDashboardTests`（6） | 等待积压与未清除的结构性阻断分两组；未纳入 AREA 不显示为告警；已清除阻断不显示；四类原因有中文说明，未登记码原样显示 |
| 白名单、调度区与冻结（#72，PR #105） | `AreaAssignmentDispatchTests`（12）、`DemandAreaAssignmentFreezeTests`（+4）、`JourneyRuntimeWorkerTests`（+4）、`MultiVehicleExecutionTests`（+1） | 白名单与调度区改读归属表；未映射 AREA 静默；T 开头 AREA 被受理并冻结；评估到受理之间换版本整单拒绝；并发冻结不报库错；受理前崩溃后重放幂等 |
| 组内选仓与四类原因码（#73，PR #111） | `SlotGroupSelectionTests`（13）、`JourneyRuntimeWorkerTests`（+4） | 整单放进本组编号最小的空仓；另一组空仓不算；超出组物理仓数、AREA 未指派、车型未解析分别拒绝；组内暂不足时等待，不借另一组 |
| 结构性分级与去重（#74，PR #115） | `StructuralDispatchBlockTests`（21）、`JourneyRuntimeWorkerTests`（+1） | 分级表覆盖派车链写出的每个原因码；超大需求立即形成阻断与告警；持续期间只刷新时间，受理、离开目录或原因消失时清除；禁用或占用的仓不算结构性 |
| 车载端分组显示（onboard-hmi#66，PR #92） | `SlotGroupPresentationTests`（14，UnitTests）、`SlotGroupHighlightTests`（3，WireToGateG2Tests） | 按生效配置分前后两组显示；旧 `LEFT`／`RIGHT` 文档与缺仓归入「分组未知」并记日志；开门侧由目标仓推出；只高亮有目标仓的组 |

另有两张票的测试不守产品能力：#71（PR #108）的 `FakeOnboardSlotStateSeedTests`（4）守 L2 合成装置的逐仓状态注入；#75（PR #120）只有 L2 场景。

## 二、L2

### `l2.yml` 八行核对

八行全部由各功能票自己加入，以 `Runs = 1`、`BatchId = 'batch-4'` 进入清单，**没有漏加的行**：

| 规格 8.3 场景 | 文件 | 加入的票 |
| --- | --- | --- |
| ① 目标仓落在需求 AREA 指派的组内，组内升序 | `slot-group-selection` | #73 |
| ② 所需组空仓不足，整车空仓够也不派，归正常积压 | `slot-group-temporarily-full` | #73 |
| ③a 花篮数超过该组物理仓位数，立即结构性阻断告警 | `structural-block-oversized-demand` | #74 |
| ③b 仓位临时禁用造成的不足不告警（**负向**） | `slot-group-disabled-no-alert` | #74 |
| ④ 导入五类配置错误各自整份拒绝（**负向**） | `area-assignment-import-rejects` | #68 |
| ⑤ 新版本不停车生效，在途需求按冻结的旧版本开门，预览列出变化 | `area-assignment-version-freeze` | #75 |
| ⑥ 未纳入的 AREA 静默跳过、不报警（**负向**） | `area-assignment-unmapped-silent` | #72 |
| ⑦ 同一站点挂前后两侧 AREA，先后两趟各开各组 | `mixed-side-station-two-trips` | #75 |

本票把八行改为 `Runs = 3`，`BatchId` 不变，其它批次的行不动。PR #119（2026-09-17）之后，`Runs` 只在手动三连（`mode=consecutive`）时生效，
PR 与 push 上每个场景仍跑一遍；批次出口用 `mode=consecutive-all`，每个场景至少三遍。

**③a 的构造（规格 8.5）**：超大需求在现场是否出现取决于 MES 自然产生 5～8 个花篮的需求，所以这条证据由 L2 构造，不以「现场没触发」算通过。

**⑦ 的形状与票面不同**：场景现在是「两台车各走一趟」，不是票面的「一台车先后两趟」。原因是合成车载端按固定键缓存批次录入应答，
一台假车跑不完两趟（第四节 R-4）；#75 接受了这个临时形状。它要证的是「服务端不按站点检查两侧 AREA 的一致性」，与哪台车停靠无关。
修复与改回单车两趟在 PR #121，**本出口时尚未合入**。

### 作业超时

| 触发 | 改前 | 改后 |
| --- | --- | --- |
| `workflow_dispatch`（含 `consecutive-all`） | 180 分钟 | 180 分钟（不改） |
| `pull_request`／`push` | 60 分钟 | 60 分钟（不改） |

依据是实测耗时：

- 批次 4 八个场景各一遍，在 CI run `35190879677`（2026-09-17，默认模式）里从 07:03:04 跑到约 07:09，约 6 分钟。
- 本票三连 run `35196671017` 八个场景各三遍，作业实测约 18.5 分钟（08:06:34～08:25:02）。
- 整张清单默认模式一遍约 19 分钟（该 run 时清单为 25 个场景，现为 26 个）（run `35190879677` 作业 06:50:44～07:09:32）；即使 `consecutive-all` 不带场景过滤跑全清单三遍，按比例约 55～60 分钟，仍远低于 180 分钟。
- PR 默认模式每个场景仍只跑一遍，本票的 `Runs = 3` 不增加 PR 耗时，60 分钟不需要动。

规格写「作业超时按三连实测耗时调大」时，手动三连的 180 分钟上限还不存在；PR #119 已经把它放宽到位，本票按实测确认够用，不再改动。

### 三连运行

| 运行 | commit | 结果 | 证据 |
| --- | --- | --- | --- |
| **CI `35196671017`**（`mode=consecutive-all`，八个场景） | **`03ec8a8c`** | **24/24 PASS，job success**，八个场景各 3/3 | `evidence/l2/20260917-ci-35196671017-<场景>-0{1,2,3}`（24 个目录，从该 run 的 artifact `l2-evidence` 原样取回） |

| 场景 | 规格 8.3 | 判据 | 三遍结果 | 证据目录 |
| --- | --- | --- | --- | --- |
| `slot-group-selection` | ① | 9 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-slot-group-selection-01`、`-02`、`-03` |
| `slot-group-temporarily-full` | ② | 7 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-slot-group-temporarily-full-01`、`-02`、`-03` |
| `structural-block-oversized-demand` | ③a | 8 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-structural-block-oversized-demand-01`、`-02`、`-03` |
| `slot-group-disabled-no-alert` | ③b（负向） | 5 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-slot-group-disabled-no-alert-01`、`-02`、`-03` |
| `area-assignment-import-rejects` | ④（负向） | 15 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-area-assignment-import-rejects-01`、`-02`、`-03` |
| `area-assignment-version-freeze` | ⑤ | 15 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-area-assignment-version-freeze-01`、`-02`、`-03` |
| `area-assignment-unmapped-silent` | ⑥（负向） | 7 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-area-assignment-unmapped-silent-01`、`-02`、`-03` |
| `mixed-side-station-two-trips` | ⑦ | 9 条 | 3/3 PASS | `evidence/l2/20260917-ci-35196671017-mixed-side-station-two-trips-01`、`-02`、`-03` |

24 份证据的 `batchId` 都是 `batch-4`，`controlServerCommit` 都是 `03ec8a8c`，每份有自己的 `runId` 与 stage root；作业 08:06:34～08:25:02（UTC），没有红、没有重跑。

### 协议身份

本票三连的 24 份证据与真装置证据，`identity.protocolReleaseIdentity` 读回都是：

| 项 | 值 |
| --- | --- |
| tag | `protocol-v2.0.0` |
| commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolVersion | `3` |
| manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| approvalStatus | `SUPERSEDING_CANDIDATE` |

票面预期的是两种情况之一：批次 4 先于批次 5 出口则绑 `protocol-v1.0.0`，否则绑已发布的 `v2.0.0`。**实际是第三种**：集成分支 `fp/v2-impl`
已经带着批次 5 的 `v2.0.0` 候选（PR #107、#109），但 `v2.0.0` 还没有发布。所以本批次证据绑的是**未发布的候选身份**。
批次 5 发布 `protocol-v2.0.0` 之后，由批次5-36（[control-server#90](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/90)）在发布身份上随全部场景重跑
（规格第 16 节第 12 条），这是计划内的重跑，**不是重开批次 4**。「v2 线」不等于 `protocol-v2.0.0`（规格 8.8 第 5 条）。

### 真装置 L2

| 场景 | 绑定 | 结果 | 证据 |
| --- | --- | --- | --- |
| `real-onboard-normal-load` | 服务端 `03ec8a8c`、车载端 `08bcf7d`、`slots-simulator@fb5f7c5`、协议同上 | **PASS**，12/12 | `evidence/l2/20260917-b4-12-real-onboard-normal-load-001` |

这一跑用含批次4（第二版）-02 的车载端，证明「前后两组分开显示」没有打断 UI Automation 驱动的录入路径：UIA 写入的 sublot 以 `KEYBOARD`
方式提交到服务端，装卸走真 Modbus 闭环，需求终态 `Succeeded`。它能证明的是「没坏」，**不证明按侧开门**，也不代表真车、真 IO 模块或接线合格。
按工作区 `CLAUDE.md`「Real-rig L2」在 `repos/` 克隆里分离检出三个提交后运行，跑完三个克隆已切回 `fp/v2-impl`、`w2g/fp-v2-impl`、`main`，均干净。

## 三、门禁：未跑及理由

批次 4 没有切片，出口不含门禁。`CONTROL_SERVER_G2`（`scripts/test-wire-to-gate.ps1`）对 `FP-IS-01`／`02` **未跑**，理由：

- 批次 4 改了这两片路径上的派车选仓，但门禁只能证明「批次 4 没把这两片弄坏」，证不出按侧开门（规格 7.4 第 11 条）；按侧开门的证据由上面的 L2 承担。
- 代价是按片出证的整套运行与证据整理。
- 批次 5 发布 `protocol-v2.0.0` 后，这两片无论如何都要在新身份上重过四道门禁（规格第 6.5 节）；现在跑出的证据绑的是未发布候选，届时作废。

## 四、红证据与缺陷单

**本票自己的运行没有红**（L1 两端、真装置 L2、CI 三连）。每次运行的 `-EvidenceRoot` 都是新目录。

批次 4 期间各功能票留下的红，逐条对上记录：

| 红 | 性质 | 记在哪 |
| --- | --- | --- |
| `evidence/l2/20260916-b4-09-slot-group-selection-001` | 场景脚本：管道拼接、第二条需求 EQP 不唯一 | `docs/defects/20260917-batch-4-red-l2-runs-registered-at-exit.md` R-1（本票补写） |
| `evidence/l2/20260917-b4-10-structural-block-oversized-demand-001` | 场景脚本：结果集被包成单元素数组 | 同上 R-2（本票补写） |
| CI run `35109762084`（`command-surface-order-hold` 第 3/3 次） | 场景：断言早于假 RIoT 收到请求，已由 PR #113 修复 | 同上 R-3（本票补写） |
| `evidence/l2/20260917-b4-11-mixed-side-station-two-trips-red-001` | 测试替身：假车载端按固定键缓存应答，修复在 PR #121（未合入） | 同上 R-4（本票补写） |
| CI run `35055524167` attempt 1（`session-established-while-moving`） | **产品缺陷**，已修 | `docs/defects/20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md` |

前四条的诊断原本只写在 PR 正文或 issue 评论里，本票按「失败原因在 `docs/defects/` 有记录」补成一张单，诊断原样沿用，没有重新定性。
#75 的关闭评论还记了一次合并后本地全量 L1 出现 1 个失败（1087/1088）、未记下用例名、之后未复现；本票 L1 一遍 1093/1093，也未复现。

## 五、必须如实写明的各点

### 1. 批次 4 推翻了架构不变量 I8，属重构

I8 是「仓位可互换，任何仓位服务任何站点」。按规格第 4.1 节点名的位置，逐条给出合入后的新位置（路径省略 `src/ControlServer.` 前缀）：

| 规格点名（批次 3 形态） | 合入后 | 说明 |
| --- | --- | --- |
| `SlotCapacityCriterion.cs:77-82`：从全部空仓按编号取前 N 个 | `Host/Runtime/Dispatch/Criteria/SlotCapacityCriterion.cs:99-130` | 取需求的 `RequiredSlotPosition`，组内过滤、升序取前 N；AREA 未指派、车型未解析、超出组物理仓数分别给原因码。旧写法已删除 |
| `JourneyRuntimeEngine.cs:1395-1401`、`:1418-1425`：空仓事实只有编号 | 原样保留（现 `:1486-1490`、`:1507`）；分组另读：`JourneyRuntimeEngine.cs:343-356` | 规格 19.7 核实后不必改。分组取服务端权威 `IVehicleSlotPositionReader`，每轮每车读一次，不取车报 |
| `DispatchAdmission.cs:69-76`：`AvailableSlots` 是 `int[]` | `Host/Runtime/Dispatch/DispatchAdmission.cs:80`、`:140` | `int[]` 保留；新增 `SlotPositions` 与 `RequiredSlotPosition => AreaAssignment?.SlotPosition`。读取器在 `Infrastructure/Persistence/AreaAssignmentStores.cs:393-417`，先看生效配置、再看最新已发布 IO 绑定，都没有则「未解析」，不回退默认八仓 |
| `SlotConfigurationAuthorityStore.cs:223` 种子写 `LEFT`／`RIGHT`；`Runtime/` 不读 `SlotPosition` | `Infrastructure/Persistence/SlotConfigurationAuthorityStore.cs:230` 写 `FRONT`／`REAR`；`SlotCapacityCriterion`、`StructuralDispatchBlockSink.cs:195-199` 读 `SlotPosition` | 非迁移代码里已无 `"LEFT"`／`"RIGHT"` |
| 站点上没有开门侧事实（`Ports.cs:247` 的 `RiotMapStation`；`MapStationResolver.cs` 从站名切 AREA） | `Application/AreaAssignmentPorts.cs:25` 的 `AreaAssignment(Area, DispatchZone, SlotPosition)`；`JourneyRuntimeEngine.cs:257-260` 每轮读一次；`Host/Runtime/Dispatch/Criteria/AreaAssignmentLookupCriterion.cs:32-34` 填到评估对象；`StationResolutionCriterion.cs:57` 调度区取 `assignment.DispatchZone` | 开门侧按 AREA 定、不按站点定（规格 5.1），所以 `RiotMapStation` 有意不动；AREA 正则收拢到 `Domain/AreaCodeFormat.cs:15` |

推翻范围（规格 4.1）：在 AREA 指派的组内取仓、组内按编号升序；分组取服务端权威；已落库的 `LEFT`／`RIGHT` 改为 `FRONT`／`REAR`。
**不动**协议，不动车载端开门执行，仓位配置指纹继续不含 `SlotPosition`。

### 2. `REQ-0191`：从「只执行 N 开头的 AREA」到分区归属表

批次 3 在 v2 线上的形态是 `AreaScopeCriterion.cs:21` 的 `LiveMesFields!.Area!.StartsWith('N')`，不满足即 `OUT_OF_SCOPE_AREA`。

- **为何碰巧成立**：`mapId` 25 上执行的 11 个 AREA 全以 N 开头（program 仓 `docs/specs/full-product-implementation-profile-v2.tsv` 的 `REQ-0191` 行）。
- **为何不成立**：装片机台的 AREA 以 T 开头，无论怎么配置都会被这条前缀规则拒绝；产品负责人 2026-09-13 确认（规格 18.3 control-server#49 行、#72 正文）。
- **现在**：`Host/Runtime/Dispatch/Criteria/AreaScopeCriterion.cs:32-35` 只看需求 AREA 在不在分区归属表里，表就是全部白名单，没有前缀规则（#72）。
  T 开头 AREA 被受理并冻结由 `JourneyRuntimeWorkerTests` 守着。
- **T 开头 AREA 的归属与开门侧是现场参数**，由批次4（第二版）-03（[control-server#67](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/67)）在投运前定，见本节第 10 点。

### 3. 已落库的 `LEFT`／`RIGHT` 改为 `FRONT`／`REAR`

迁移 `20260915135043_Batch4AreaAssignmentAndStructuralDispatchBlock`（`Infrastructure/Persistence/Migrations/…cs:84-85`）只执行两条
`UPDATE SlotModelSlots SET SlotPosition = …`。`GovernedConfigurationSnapshots` 的内容与摘要、业务审计与管理员审计**保留原文**（`REQ-0271` 不可改写），
不发布整车模型第 2 版。`Batch4MigrationDisciplineTests` 断言改名前后快照与审计逐字节相同。

**这是同一物理事实的术语更名**：1～4 号仍是同一侧仓门，5～8 号仍是另一侧，不是硬件变化。

车载端的对应注意点（onboard-hmi#66 关闭评论）：`active-slot-configuration.json` 里 `SlotPosition` 仍为 `LEFT`／`RIGHT` 的旧文档，界面会一直显示「分组未知」；
经 `06-deploy-onboard-hmi.ps1` 整目录部署不会遇到，原地覆盖安装时要按该评论的步骤清掉旧文档。

### 4. 分侧约束不可单独关闭

分侧约束关掉就会开错门，所以不适用「每个能力一个开关」（规格 5.1「能力开关豁免」、8.7 表「分侧约束｜不可关」）。本批次没有为它加开关。

### 5. 两处有意的行为变化，不是倒退

- **5～8 个花篮的需求改人工送。**一侧只有四个仓，这类需求在分侧后装不下，按结构性派车阻断立即告警、不派车（③a）。
  这类需求的占比由 #67 查生产库得出，**未查到**：#67 仍为 OPEN，没有任何统计结果（该统计要只读查 factory01 生产库，属于要先问用户的动作，至今未执行）。
- **持货等单属于批次 7，本批次未引入。**批次 4 仍是一趟一单。

### 6. 混挂站点的真装置 L2 在批次 7

「一次停靠前后两侧各一条需求」要到批次 7 引入多停靠计划后才可能出现。本批次按规格第 16 节第 9 条，用合成装置证「同一站点挂前后两侧 AREA 时，先后两趟各开各组、不告警、不阻断」（⑦）。
⑦ 当前是两台车各一趟的形状，见第二节。

### 7. `REQ-0210` 部分实施

| 部分 | 状态 | 位置 |
| --- | --- | --- |
| 分级：正常积压、静默、结构性 | 已实施 | `Host/Runtime/Dispatch/StructuralDispatchClassification.cs` |
| 结构性立即告警 | 已实施 | `Host/Runtime/Dispatch/StructuralDispatchBlockSink.cs`，当轮形成、写 Warning |
| 去重 | 已实施 | 主键 `(DemandId, ReasonCode)`；持续期间只刷新 `LastSeenAt`，清除后再成立算新一轮 |
| 积压展示等待状态 | 已实施 | `Host/Dashboard/DispatchBacklogQueryEndpoint.cs`、`Dashboard/DispatchBacklogCard.cs`（#70） |
| **达到分区防饥饿阈值后升级告警** | **未实施** | 随 `REQ-0203` 在批次 7 做，阈值是批次 7 的投运参数（规格 19.5；`StructuralDispatchBlockSink.cs:47-48` 注释写明） |

### 8. `REQ-0208` 电量半边未实施，去向批次 9

批次 4 只实施了 `REQ-0208` 的仓位半句（按组判容量）。

- **电量半边**要求「预计完成任务后仍能保留经批准的最低电量余量」。
- **今天的形态**是比较**当前**电量：`Host/Runtime/Dispatch/Criteria/VehicleDynamicFactsCriterion.cs:89-98`（电量未知给 `BATTERY_FACT_UNKNOWN`；
  充电中或 `BatteryPercent < MinimumBatteryPercent` 给 `BATTERY_POLICY_NOT_SATISFIED`），阈值是配置默认值 `JourneyRuntimeOptions.MinimumBatteryPercent = 30`。批次 4 未改此文件。
- **成立条件**：只在一趟一单的固定行程下，「当前电量够」才近似「做完后仍够」。
- **去向**：阈值归 `REQ-0282` 的充电策略版本（批次 9），`REQ-0282` 不允许开发默认值，批次 9 之前没有已批准阈值。依赖链为批次 7 → 8 → 9 → 切生产，不影响切换。

**批次 7 引入多停靠计划后「一趟一单的固定行程」不再成立。批次 7、8 的出口报告须照写这一条：电量半边未实施、今天的形态与成立条件、去向批次 9。**

### 9. 迁移登记

批次 4 只有一个迁移，即 #66 的 `20260915135043_Batch4AreaAssignmentAndStructuralDispatchBlock`。#68～#75 均**没有追加迁移**。
同期集成分支上的 `20260917015519_Batch5JourneyBlockReasonSince` 属批次 5（control-server#80），不计入批次 4。
`Batch4MigrationDisciplineTests` 断言批次 4 恰好新增一个迁移。

### 10. 协议仓零改动

批次 4 的服务端 PR（#93、#96、#102、#105、#108、#111、#114、#115、#120）与车载端 PR #92 都没有改协议仓，也没有发现契约问题，没有开协议仓 issue。
原因码只在服务端与看板内使用，不经协议下发。

### 11. 批次4（第二版）-03 的三份现场记录

[control-server#67](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/67)（投运前现场前置与分区归属表参数）**未完成**：票为 OPEN，
约定的 `evidence/field/<日期>-B4-site-prerequisites/` 不存在，三份记录（5～8 个花篮需求占比、拆站核对、分区归属表参数与批准）都还没有。
它不挡批次 4 出口，挡的是生产切到 v2 线。

## 六、剩余风险

| 风险 | 说明 |
| --- | --- |
| 证据绑未发布候选 | 见第二节「协议身份」，#90 在发布身份上重跑 |
| ⑦ 不是票面的单车两趟 | PR #121 合入后改回；届时该场景需要重新三连（#90 的全场景三连会覆盖） |
| 装置里抄了服务端规则 | #71 关闭评论：合成装置里「可用仓」判据、AREA 正则、仓位模型选取顺序各有一份副本，服务端改规则时要同步 |
| `slot-group-temporarily-full` 依赖编排器局部变量 | #73 关闭评论：重启假车载端时经 PowerShell 动态作用域读编排器变量，编排器改名即坏（会报错，不会静默） |
| 单车装不下的判定口径 | #74 关闭评论：按全体车辆该组物理仓位最大值比较；别的分区有车够而本分区车永远装不下时，不判结构性 |
| 版本变化与目录变化同码 | #72：归属表版本变化沿用 `FINAL_CATALOG_DECISION_FACT_CHANGED`，积压里分不清是 MES 目录变化还是归属表变化 |
| 车载端旧 `LEFT`／`RIGHT` 文档 | 见第五节第 3 点 |
