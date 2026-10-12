# cs#659 证据摘要：路网黏住的陈旧原因不再被刷新失败盖掉，并有受治理的解除出口

## 问题

路网有两条陈旧原因按设计是「黏住」的：边组指纹变化（`ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED`）与动态代价由空变非空
（`ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED`）。`RouteGraphSnapshotStore.MarkStaleAsync` 直接覆写原因，于是黏住之后一次刷新失败
把原因改成 `ROUTE_GRAPH_REFRESH_FAILED`，下一次刷新成功把它清空，派车恢复——用的是一张没人看过的图。反过来，这两条原因除了直接改库没有任何解除路径。

## 改法

1. 黏住集合放到 Application 层 `RouteGraphStickyStaleReasons`；`MarkStaleAsync` 在库里是黏住原因、新原因不黏住时不写；写之前先从库重读这一行，
   免得刷新器上下文里的旧副本让一次真正的写入被当成「没变化」。两条黏住原因之间仍是后一条覆盖前一条（有意保持）。
2. 解除入口 `GET`／`POST /api/field-ops/v1/route-graph-sticky-stale-releases`，判定在 `RouteGraphStickyStaleRelease`，凭据与开关同故障人工恢复入口。
   POST 要具名操作员、理由、核实记录，外加照 GET 填的四项确认（原因、起始时刻、边组指纹、动态代价有无）；设计态必须在陈旧起始时刻之后重读过；
   清除是带条件的更新，刷新器同刻改了状态就 0 行、拒绝。每次 POST 写一条管理员审计（`ROUTE_GRAPH_STICKY_STALE_RELEASE`，
   `GovernedObjectKind.RouteGraphSnapshot`），detail 单列放行时动态代价有无。

## 测试

新测试类两个，共 24 条，均已登记进 `IntegrationSliceTraitArchitectureTests` 的账本：

- `RouteGraphStickyStaleTests`（18 条）：审查探针（指纹变化／动态代价出现 → 超时或 `HttpRequestException` → 成功，仍挡在黏住原因上，共 4 条）、
  两条黏住原因先后出现时后一条覆盖前一条、解除后刷新器再判出黏住原因时库里照写、解除恢复派车并审计、放行时动态代价仍在、设计态未在变化后重读、
  非黏住原因不归人解除、四项确认逐项不符（4 条）、清除与刷新器同刻写时刷新器胜、缺字段、无快照、每个拒绝码有中文说明。
- `RouteGraphStickyStaleReleaseEndpointsTests`（6 条）：503、401、422、GET 读状态不写审计与 404、409 带码与中文说明并审计、200 并审计。
- 另在 `StationExclusivityReleaseEndpointsTests.TheEntryIsMappedOnlyWithTheFaultRecoverySwitchOn` 加一行：新路由跟随故障恢复开关。

本机定向测试（`01-targeted-tests.txt`）：过滤 `RouteGraph`、`StationExclusivityReleaseEndpointsTests`、`ArchitectureTests`、
`Batch2CapabilityStoresTests`、`StructuralDispatchBlockTests`，292 条通过、0 条失败，退出码 0。没有跑本机全量，全量以 CI 为准。

## 变异验证

每条单独施加、重建、跑定向测试（过滤 `RouteGraph`、`StationExclusivityReleaseEndpointsTests`、`ArchitectureTests`，169 条），然后还原。
18 条全部变红。

| 变异 | 变红的用例 |
| --- | --- |
| M1 存储层去掉「失败不盖黏住原因」（等于修前行为） | 审查探针 4 条：指纹变化 × 超时／`HttpRequestException`、动态代价 × 超时／`HttpRequestException` |
| M2 `MarkStaleAsync` 不从库重读 | 解除后刷新器再判出黏住原因时库里照写 |
| M3 黏住原因也不被另一条黏住原因替换 | 两条黏住原因先后出现时后一条覆盖前一条 |
| M4 刷新器的陈旧判定不认黏住原因 | `RouteGraphEngineTests` 既有 3 条，加本票 14 条 |
| M5 解除不查当前是否黏住原因 | 非黏住原因不归人解除 |
| M6 解除不比对四项确认 | 四项确认逐项不符 4 条，加 HTTP 409 用例 |
| M7 设计态刷新时刻等于陈旧起始时刻也放行（`<=` 改 `<`） | 设计态未在变化后重读 |
| M8 去掉设计态重读检查 | 设计态未在变化后重读 |
| M9 清除的条件更新不看边组指纹 | 清除与刷新器同刻写时刷新器胜 |
| M10 条件更新到 0 行仍报解除成功 | 清除与刷新器同刻写时刷新器胜 |
| M11 拒绝不写审计 | 缺字段、无快照、设计态未重读、同刻写、HTTP 409，共 5 条 |
| M12 审计 detail 不单列放行时动态代价有无 | 解除恢复派车并审计、放行时动态代价仍在 |
| M13 不要求四项确认 | 缺字段 |
| M14 GET 不鉴权 | HTTP 503、401 |
| M15 新路由不挂在故障恢复开关上 | 路由跟随开关（`enabled: "true"`） |
| M16 一个拒绝码缺中文说明 | 每个拒绝码有中文说明 |
| M17 确认的原因不参与比对 | 确认不符（原因） |
| M18 确认的起始时刻不参与比对 | 确认不符（起始时刻） |

修前代码等于 M1 加「没有解除入口」：审查探针在修前红，与票面一致。

测量上的两个坑，记下免得复跑时重踩：

- 第一轮里 M1、M2、M4、M5 用常量 `false` 替换条件，编译器报 CS0162（不可达代码，警告即错误），构建失败，等于没测；M11 去掉 await 报 CS1998。
  改成运行时才判定的假条件（例如 `mapId == int.MinValue`）后重跑，全部变红。
- 第一轮里 M14 的匹配串在文件里出现两次，施加脚本断言失败，但脚本用 `python … | tr -d '\r'` 取结果，管道把 Python 的退出码吞了，
  于是显示「通过」，实际什么都没改。加 `set -o pipefail`、改用只在 GET 里出现的上下文定位后重跑，变红。

## 剩余风险

- 动态代价仍在时放行等于接受它：检测只看由空变非空，它一直在就不会再触发。审计 detail 单列 `dynamicRouteCostPresentAtRelease`，GET 也显示它，事后能查。
- 操作员判断「自建图仍能如实回答可达性」靠的是人：引擎不建模边组，服务端核对不了这个判断本身，只核对人看的是当前这一份状态、图在变化之后重读过。
- 服务端停机时没有解除路径（不做 FieldOps 停服写库，调度同意）。停机时刷新器也不跑，不存在「卡住」要解；要的话另开票。
