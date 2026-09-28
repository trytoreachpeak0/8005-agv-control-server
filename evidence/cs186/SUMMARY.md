# cs#186 Map 级改名检测：证据汇总

提交：先红 `02000082`（只含测试、假 RIoT、L2 场景与让测试编译得过的空壳签名），实现 `082a6115`。

## 1. 真 RIoT 只读核实（第一步）

目录：`evidence/field/2026-09-28-cs186-map-list-endpoint-check/`，两次都经用户批准（调度转达），都只发批准的请求、非 2xx 不重试。

| 时间 | 请求 | 结论（读到的） |
| --- | --- | --- |
| 09-28 23:07 | `getALLMapInfoExcludeMapJson`、`mapInfo/26` | 列表接口可用；不带 `mapJson`（2101 字节 对 264292 字节）；26 号图名两边逐字节相同：`老厂前线new_wk` |
| 09-28 23:51 | `getALLMapInfoExcludeMapJson`（存原始响应，url 换成占位） | `source`／`state`／`syncState` 都是字符串，取值 fetch／upload、activated、asynced／partition，都在 SDK 枚举里；响应哈希与 23:07 相同 |

这份原始响应复制为 `tests/ControlServer.Tests/RiotReplays/map-list-2026-09-28.json`，由 `HttpRiotMapNameCatalogTests` 用生产适配器回放；另一格把三个枚举字段换成 SDK 不认识的值，仍读出 8 张图（读到的，不再是推测）。

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
