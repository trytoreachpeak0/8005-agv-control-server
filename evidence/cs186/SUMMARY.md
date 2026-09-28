# cs#186 Map 级改名检测：证据汇总

提交：先红 `02000082`（只含测试、假 RIoT、L2 场景与让测试编译得过的空壳签名），实现 `082a6115`。

## 1. 真 RIoT 只读核实（第一步）

目录：`evidence/field/2026-09-28-cs186-map-list-endpoint-check/`，两次都经用户批准（调度转达），都只发批准的请求、非 2xx 不重试。

| 时间 | 请求 | 结论（读到的） |
| --- | --- | --- |
| 09-28 23:07 | `getALLMapInfoExcludeMapJson`、`mapInfo/26` | 列表接口可用；不带 `mapJson`（2101 字节 对 264292 字节）；26 号图名两边逐字节相同：`老厂前线new_wk` |
| 09-28 23:51 | `getALLMapInfoExcludeMapJson`（存响应，url 换成占位） | `source`／`state`／`syncState` 都是字符串，取值 fetch／upload、activated、asynced／partition，都在 SDK 枚举里；响应哈希与 23:07 相同 |

第一次读取的证据提交在 `af3cdb8e`；第二次（`real-riot-raw/`）提交在 `082a6115`，不在 `af3cdb8e`（审查更正）。

**更正（审查第 6 条）**：`real-riot-raw/map-list.json` 不是「原始字节只换了 url」。抓取脚本先用 `ConvertFrom-Json` 解析、把 url 换成占位，再用 `ConvertTo-Json` 重新序列化写出，所以缩进、换行与空白都与 RIoT 原样不同；键、值与值的 JSON 类型不变（逐字段类型记在 `value-types.json`，是从原始响应解析出来的）。原始字节只留下了哈希（`requests.jsonl` 的 `sha256`），没有留下字节本身。

这份响应复制为 `tests/ControlServer.Tests/RiotReplays/map-list-2026-09-28.json`，由 `HttpRiotMapNameCatalogTests` 用生产适配器回放；另一格把三个枚举字段换成 SDK 不认识的值，仍读出 8 张图（读到的，不再是推测）。回放证明的是「这些键、这些值、这些类型能被解析」，不证明「RIoT 原样字节能被解析」——两者只差空白与键间格式，JSON 解析器不看这些（推的）。

## 2. L1：先红

`l1-red-on-02000082.txt`：在先红提交上跑本票的测试类，39 红 6 绿。红全部落在断言上（没有编译错误、空引用或夹具异常），6 条绿是切片账本检查与假 RIoT 的参数校验。逐条的红点与行号见该文件的 `Failed` 块。

## 3. L2：`map-rename-holds-all-task-types`

| 目录 | 代码 | 结论 |
| --- | --- | --- |
| `evidence/l2/20260929-cs186-map-rename-red-02000082` | 先红提交：产品行为与 `fp/v2-impl`（`2419ecfd`）相同 | FAIL：L2-MR-03（改名后应有两条 `MAP_RENAMED` 暂停，实际 `(none)`）、L2-MR-04（新需求应被 `TASK_TYPE_HELD` 挡住，实际 `AwaitingPickupArrival`，照常派车）红，之后各条随之红，场景在 `accept-map-name` 处抛错结束 |
| `evidence/l2/20260929-cs186-map-rename-green-082a6115` | 实现 | PASS，9/9 |

只入库 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`；`logs/` 与 `snapshots/` 未入库。L2-MR-01（读失败不算改名）在旧代码上按构造就是绿的（旧代码根本不读地图列表），它的判别力由变异 M2、M3、M12 在 L1 上给出。

## 4. 变异

脚本 `mutations/run-mutations.py`，记录 `mutations/results.json`，每个变异的构建与测试输出 `mutations/M*.build.log`、`M*.test.log`。每个变异跑之前写下预期红的用例；替换必须恰好命中一处，否则整批中止；`dotnet build --no-incremental` 后 `dotnet test --no-build`；从备份还原并刷新时间戳；红用例名从输出读出，并与汇总的 Failed 数对账。

| 编号 | 模拟的失败 | 预期红 | 实际红 | 说明 |
| --- | --- | --- | --- | --- |
| M1 | 列表里的空名当作数据 | 2 | 2 | |
| M2 | 读不到列表，按「所有已知图都改名」处理 | 3 | 3 | 票面要求的「读失败不算改名」 |
| M3 | 列表里缺席的图当作改名 | 2 | 2 | |
| M4 | 首次读到也暂停 | 8 | 10 | 多红 2 条：「别的图不受影响」（25 号图首读即被暂停）与审计逐条核对（首读多写了暂停审计）。都是在对的地方挡住，预期漏算 |
| M5 | 用 `mapIdentity` 做基线初值 | 4 | 4 | |
| M6 | 暂停全部 6 个任务类型而不只是有绑定的 | 8 | 8 | |
| M7 | 解除动作不再查未接受的改名 | 1 | 1 | 第一次注入写法编译不过（CS8602），整次无效，记录保留为 `M7-invalid-first-attempt`；改写后重跑 |
| M8a | 服务层不再比对输入名与待接受名 | 0 | 0 | 预期存活：存储层条件写仍挡住，是两层防护 |
| M8b | 存储层接受任何名称 | 1 | 1 | |
| M9 | 改回原名时自动解除暂停 | 1 | 1 | |
| M10 | 真响应里 `source` 变成数字（改夹具） | 4 | 4 | |
| M11 | 适配器吞掉读失败、回空名单 | 4 | 4 | |
| M12 | 读失败时中断这一轮 | 3 | 3 | |
| M13 | 名称比较忽略大小写 | 1 | 1 | |

变异跑完工作区 `src`、`tests`、`tools` 无改动（脚本末尾的 `git status` 为 clean）。

## 5. 其余

- cs#342 模型测试 `ReconnectModel*`：23/23 绿（`cs342-model-on-082a6115.txt`）。
- 本票相关与相邻测试类 256 条全绿；改动文件 `dotnet format --verify-no-changes` 无差异。
- 服务端全量测试走 CI（转 ready 后那一轮）。

## 6. 审查轮次（PR #378 两路独立审查，修后可合）

提交：先红 `45d66107`（只含测试，另加一个构造参数的空壳），修复 `08d1a39c`。

| 审查项 | 做了什么 | 证据 |
| --- | --- | --- |
| 必修 M1：读失败时暂停仍须挡住派车，要断在派车结果上 | `MapRenameEngineTests.AfterARenameAFailedMapListReadDoesNotLetTheHeldTaskTypeDispatch`：改名、暂停后读失败两轮，没有旅程、没建单、积压原因码 `TASK_TYPE_HELD` | 在现有代码上是绿的；变异 MX2（读失败时放掉 MAP_RENAMED 暂停）只让它一条红 |
| 建议 1：解除的待接受检查在事务外 | 解除的写事务里再读一次，有值就不解除、拒绝 `MAP_RENAME_NOT_ACCEPTED` | `ARenameSeenBetweenTheReleaseChecksAndItsTransactionStillRefusesTheRelease` 先红后绿；变异 R1 红 1 |
| 建议 2：待接受期间激活新绑定 | 选「激活事务里直接挂暂停」：激活第二步（新版本生效）的事务里，若有待接受名称，给新版本绑定的每个任务类型挂 `MAP_RENAMED` 暂停。不选「拒绝激活」，因为改名若在激活两步之间才被读到，拒绝的检查早已过去 | `AnActivationUnderAPendingRenameHoldsEveryTaskTypeOfTheVersionItMakesActive`（改名在激活前／在两步之间）先红后绿；变异 R2 红 2 |
| 建议 3：一张图出错连累后面的图 | 逐图 try/catch，出错的图记日志 2197、本轮跳过 | `AFailureOnOneUnrelatedMapDoesNotKeepAMapAfterItFromBeingObserved` 先红后绿；变异 R3 红 1 |
| 建议 4：MX1 与两个前提用例的积压原因码 | (a) 断 `TASK_TYPE_HELD`，(b) 断 `TASK_TYPE_BINDING_MISSING` | 变异 MX1 红 2 |
| 建议 5：L2-MR-01 要证明窗口里确实读过、确实 500 | 假 RIoT 快照加 `mapListReads`、`mapListServerErrors`，判据断言两者在窗口内都增加 | `evidence/l2/20260929-cs186-map-rename-green-08d1a39c`：9/9 PASS，窗口内读 3 次、500 共 3 次 |
| 建议 6：说法更正 | 第 1 节已更正（夹具是重新序列化的；`real-riot-raw` 在 `082a6115`）；场景头注释改为「红版本上 accept-map-name 调到空壳、答 REJECTED」 | — |
| 建议 7 | `l2.yml` 的 `$estimates` 登记本场景 95 秒 | — |

先红：`l1-review-red-before-fix.txt`（6 条里 4 红 2 绿，绿的两条就是上表说明的那两条）。修复后本票与相关测试类加 cs#342 模型测试 408 条全绿：`l1-review-green-08d1a39c.txt`。

### 变异重跑（`08d1a39c`，`mutations/results-08d1a39c.json`）

全部 19 个变异（原 14 个加 MX1、MX2、R1、R2、R3）在修复后的代码上重跑，每个都事先写下预期；没有一个漏红（`expectedButGreen` 全空），工作区跑完还原干净。与第一批不同的四处：

- **M7 由红 1 变为存活**，这是预期写好的：解除的写事务现在自己也会拒绝（建议 1），只去掉服务层的前置检查挡不住任何事，与 M8a 同样是两道防护。
- **M6 多红 1 条**：`AFailureOnOneUnrelatedMap…` 断言 26 号图恰好一条暂停，M6 让它挂了 6 条。这条用例是本轮新写的，第一批预期里没有它。
- **M12 多红 1 条**：必修用例 `AfterARenameAFailedMapListRead…` 在 M12（读失败中断整轮）下以这一轮抛出的异常红，没走到断言。它挡住的是「读失败中断整轮」，不是「放掉暂停」——后者由 MX2 单独验证。
- M4 多红 2 条与第一批相同，理由见第 4 节。

另：重跑时发现 `dotnet format` 把几个源文件在工作区写成了 CRLF，跨行锚点因此匹配 0 处；脚本按设计整批中止，没有带着没生效的注入往下跑。脚本已改为按文件的行尾适配锚点，M3 的锚点改短（它后面现在紧跟的是新加的 `ConvergeIsolatedAsync`）。中止那次只跑完了 M1、M2，其记录被随后的整批重跑覆盖。

## 7. 增量审查（无必修，几处小改动）

提交：先红 `ce7559a6`，修复 `6e363d7c`，多车转录夹具 `b752f430`。

| 审查项 | 做了什么 | 证据 |
| --- | --- | --- |
| 1：启动装载预置时没补挂改名暂停 | 预置成为第一个生效版本的事务里，若该图有未接受的改名，预置绑定的任务类型挂 `MAP_RENAMED` 暂停 | `TaskTypeStationStartupTests.AFirstStartUnderAPendingMapRenameHoldsEveryTaskTypeThePresetBinds` 先红后绿；变异 R4 只红它 |
| 2：两个前提写明 | 服务层解除前置检查的注释写明它是冗余的第二道；`ObserveAsync` 遇到外层事务直接抛异常 | `ObservingInsideSomeoneElsesTransactionIsRefusedBecauseOneMapCouldNotBeRolledBackAlone` 先红后绿；变异 R5 只红它 |
| 3：低优先级 | `MapListReadCounter` 挪到 `MapStationReadCounter` 的文档之后并配自己的文档；`__pycache__` 移出版本库、`.gitignore` 忽略；L2-MR-01 描述改为「至少一次」 | — |
| 4：激活路径的暂停详情与引擎不一致 | 统一：三条路径（引擎观察、激活第二步、启动装载）都经 Infrastructure 的 `MapRenameHoldWriter` 写，详情带 `inFlightDemands`、中文不转义、`raisedThrough` 标出路径；在途需求计数移到 `TaskTypeInFlightDemandCount`（Host 的原函数转调，查询一字未改）；审计动作名只在 `TaskTypeStationHoldAuditActions.Raised` 定义一次，#162 的 `HoldRaisedAction` 引用它、值不变 | 相关测试类 427 条全绿（`l1-review2-green.txt`） |

### 变异第三轮（`6e363d7c`，`mutations/results-6e363d7c.json`）

21 个变异（前一轮 19 个加 R4、R5）全部重跑，没有一个漏红；与上一轮相比结果完全相同，新增的 R4、R5 各只红自己那一条。M4 的注入代码改为调用 `MapRenameHoldWriter.RaiseAsync`（原先调用的私有方法已随统一删除）。测试过滤加上 `TaskTypeStationStartupTests`。

### 全量测试第一次在本地跑出的问题

在 `6e363d7c` 上第一次本地跑全量（`full-suite-6e363d7c-red.txt`）：3060 条里 `MultiVehicleExecutionTests` 五条「搬动之前的转录」用例红。原因（读到的）：这组用例让车队时钟每读一次就前进一格，并把每次读到的时间戳逐字钉在转录里；本票在每一轮开头加的地图名检查多读了两次这把时钟，之后的时间戳整体后移，派车决定一个没变。从 `082a6115` 起就是如此，此前全量只留给 CI，本地没跑过——照原计划直接转 ready，CI 那一轮会红在这里。

改法（`b752f430`）：多车夹具里地图名检查用一把不走的时钟，`FleetRiot` 的地图列表不读会走的车队时钟。转录基线一字未改：在本分支上重录等于拿本票的输出当期望。

全量测试（`b752f430`）：3060 条全绿，退出码 0（含协议 Schema 一致性检查），`full-suite-b752f430.txt`。

本地合成 L2（`0122296e`，调度第二次增量审查前要求补跑）：`evidence/l2/20260929-cs186-map-rename-green-0122296e`，9/9 PASS，L2-MR-01 窗口内读 3 次、500 共 3 次。
