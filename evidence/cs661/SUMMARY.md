# cs#661 证据摘要：换 RIoT.Sdk.Facade 0.2.0-fp.6

## 换的是什么

riot-sdk#5（合并提交 `cf58c11fe`，修 riot-sdk#4）让门面 `RiotWire.ReadAsync` 回到生成层的请求路径：
响应体停住时按 `HttpClient.Timeout` 超时、失败响应会释放。本仓的路网刷新正靠这 6 个读取
（`HttpRouteGraphSource`）。代价是三处行为变化：Content-Type 不是 `application/json` 会抛错、
数字经无类型模型转一道、慢速涓流更早超时（详见 `vendor/nuget/riot-sdk/0.2.0-fp.6/README.md`）。

## 换包前只读核对（先做，调度放行后才换包）

`01-content-type-readonly-get.txt`。2026-10-12 19:54 从控制端对真 RIoT `172.19.206.222:8888` 的
6 个接口各发一次只读 GET（地图 26），路由出口是 Wi-Fi、不经 Clash，不建单、不发命令、不涉及任何车，
响应体不落盘。结果（读到的）：

- 6 个接口都是 HTTP 200、`application/json; charset=UTF-8`、业务 code `0`。
- 内存里扫数字写法：edges、stations、mapEdgeGroup 共 14578 个数，指数写法 0 个、超出 float 范围 0 个、
  int 字段写成带小数 0 个。

上次的依据是 8005-agv-program `rcs/riot-behavior-lab/evidence/rounds/2026-09-03-round-43/runs/`
（09-03 采集）；今天的结果与之一致。

## 用新包真解析（调度要求加的一步）

`02-facade-live-parse-fp6-vs-fp5.txt`。仓库外的临时控制台（不入库），用 fp.6 的门面对真 RIoT 把
6 个读取各调一次，规矩同上；同一个程序再对 fp.5 跑一遍做对照。结果（读到的）：

| 读取 | fp.6 | fp.5 | 第 1 步字节数 | 合理性 |
| --- | --- | --- | --- | --- |
| `mapInfo/edges/26` | 成功，447 条边 | 同 | 155843 | 约 349 字节／条 |
| `mapInfo/stations/26` | 成功，217 个站点 | 同 | 101405 | 约 467 字节／个 |
| `mapResource/removedEdge/26` | 成功，0 条 | 同 | 67 | 空 `result` 的信封 |
| `mapResource/removedStation/26` | 成功，0 条 | 同 | 67 | 同上 |
| `mapEdgeGroup/all` | 成功，101 个成员（4 张图、6 个组，地图 26 为 0） | 同 | 20013 | 约 198 字节／个 |
| `route/` | 成功，`Present=False`、`EntryCount=0` | 同 | 67 | 同上 |

两版返回的记录序列化后 SHA-256 逐项相同（edges、stations、edge groups、两个 removed 列表），
即今天地图 26 的真实载荷上，新包与旧包解析结果一字不差。

## 包与钉包

- 打包、nuspec 核对、`SHA256SUMS` 见 fp.6 目录的 README。五个 `packages.lock.json` 只有 RIoT.Sdk.* 三个包的
  版本与 `contentHash` 变化，三个 `contentHash` 与 vendor 包的 SHA-512 逐一相同（各 5 处）。
- `RiotSdkPackageProvenanceTests` 的 `PackageVersion`、`RepositoryCommit` 改为 `0.2.0-fp.6` 与 `cf58c11fe`。
  变异：把两个常量改回 fp.5 的值，`TheCheckedVersionIsTheVersionTheBuildConsumes` 变红（1 failed、3 passed），
  还原后 4 passed。
- 旧 fp.5 目录按既有惯例保留（fp.2～fp.5 都还在）。

## 异常分类回扫

- 范围：`git diff 4d3b438 cf58c11 -- csharp` 只有 `RIoT.Sdk.Facade/RiotWire.cs` 和一个测试文件，所以新异常只会从
  这 6 个读取冒出来。本仓只有 `HttpRouteGraphSource` 调它们（`git grep` 核过，`src/` 与 `tools/` 再无别处）。
- `HttpRouteGraphSource` 不 catch 任何异常，原样抛给 `RouteGraphRefresher.RefreshOnceAsync`。
- `RefreshOnceAsync` 的 catch 条件是 `error is not OperationCanceledException || !cancellationToken.IsCancellationRequested`：
  - `InvalidOperationException`（Content-Type 不对）、`RiotApiException`（缺 Content-Type、非有限数）都不是取消，
    一律记日志、把快照标为 `ROUTE_GRAPH_REFRESH_FAILED` 后返回，派车按快照过期挡住，下一轮再刷。
  - `TaskCanceledException`（`HttpClient.Timeout` 覆盖响应体后的超时）在调用方令牌未取消时同样标 `ROUTE_GRAPH_REFRESH_FAILED`
    （cs#656 已接住，`RouteGraphRefreshTimeoutTests`）。
  - 只有服务停机（`stoppingToken` 已取消）的取消会穿出，由 `JourneyRuntimeWorker` 结束循环。
- `JourneyRuntimeWorker` 给路网刷新单独一个 try（cs#656）：刷新抛出的任何异常都不会跳过引擎轮次。
- `RiotCallFailureClassification` 只被 `HttpRiotMovementGateway` 和 `HttpRiotOrderCommandGateway` 使用，
  它们不走这 6 个读取，不受本次换包影响。顺带记一下：它把 `InvalidOperationException` 算作 SDK 失败、类别
  `SDK_FAILURE`，`TaskCanceledException` 算 `TIMEOUT`。
- 结论（读代码得到的）：三种新异常在本仓都没有被当成「读到了、内容为空」吞掉，都走 fail-closed 的
  「快照过期 → 不派车」。现有测试 `RouteGraphEngineTests`（`HttpRequestException` → `ROUTE_GRAPH_REFRESH_FAILED`）与
  `RouteGraphRefreshTimeoutTests`（`TaskCanceledException` → `ROUTE_GRAPH_REFRESH_FAILED`）覆盖同一个 catch 分支；
  没有新增专门抛 `InvalidOperationException`／`RiotApiException` 的用例。

## 验证

- 定向测试：`--filter "FullyQualifiedName~RiotSdkPackageProvenanceTests|FullyQualifiedName~RouteGraph|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~Riot"`，
  578 passed、0 failed（读到的）。
- 证据文件入库后重跑：`ArchitectureTests` 与钉包来源 83 passed；会扫仓库文本的守卫（`AdmissionPolicyVersionGuardTests`、
  `Batch8OccupancyRetirementMigrationTests`、`CreateGateTests`、`ServerLogVolumeTests`、`SlotConfigurationReadinessGateTests`）
  80 passed（读到的）。全量交给 CI。

## 剩余风险（前提在哪里失效）

1. Content-Type 的前提是今天读到的。RIoT 升级或前面加了代理、网关改写头部时，路网刷新会整轮失败、快照过期、
   v2 不派车。表现是日志里的路网刷新失败带 `InvalidOperationException`，快照过期原因为 `ROUTE_GRAPH_REFRESH_FAILED`。
   这是失败关闭，不会派错车，但会停派。
2. 数字的前提同样是今天的载荷：地图被编辑出指数写法或超大数时，同上整轮失败。
3. `RIoT:timeoutSeconds`（默认 30 秒）现在覆盖整段响应体。今天最大的 edges 响应 155 KB、首次约 2 秒，
   离 30 秒很远；地图显著变大或网络变慢时会更早超时，结果同样是 `ROUTE_GRAPH_REFRESH_FAILED`。
4. 只核了地图 26；`mapEdgeGroup/all` 与 `route/` 是全图接口，已覆盖。
