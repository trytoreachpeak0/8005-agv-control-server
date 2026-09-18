# RIoT 调用白名单

**8005 的产品代码获准调用哪些 RIoT 端点，逐条可判定。**

| 项 | 值 |
| --- | --- |
| 状态 | 生效 |
| 批准人 | Zhengyu Shao（需求基线最终批准人） |
| 维护者 | Zhengyu Shao |
| 绑定环境 | `RIOT-8005-RUNTIME`，运行 build `v2.2.0.14` |
| 绑定契约快照 | `RIOT-OPENAPI-8005-202607-EARLY-01` |
| 本版依据 | 需求基线 `v1.1.0`（SHA-256 `5fe4b701…fb53`，tag `requirements-baseline-v1.1.0`）；第 1.5 节「解除」随需求基线 `v1.3.0`（SHA-256 `ac74c78e…bec7`，tag `requirements-baseline-v1.3.0`，`CP-0003`）跟改 |
| 首次成文 | 2026-09-08 |

## 这份文档是什么

它是**汇编**，不是新的授权。规范内容全部来自已批准的需求基线条目，本文档把散在多条 `REQ` 里
的调用面收拢成一张逐条可判定的表，并补上基线只以概括语覆盖的那几条的具体路径。

在此之前，白名单的唯一完整副本是
[`.scratch/current-requirements-baseline/issues/37-decide-riot-api-allowlist-and-call-safety-boundary.md`](../.scratch/current-requirements-baseline/issues/37-decide-riot-api-allowlist-and-call-safety-boundary.md)
——**另一张已完成地图的工作票据**。`REQ-0294` 正文里那句「现有白名单也明确禁止这些调用」指的
就是它。工作票据不该承担这个角色，这份文档取代它成为查阅入口。

### 规范层级

三层，冲突时上层赢：

1. **需求基线** `requirements/baselines/current-requirements-v1.1.0.md` —— 唯一真相源。本文档
   每一行都标注它的基线载体；没有载体的行在第三节单独登记。
2. **本文档** —— 汇编与展开。它可以比基线更具体（把概括语展开成路径），**不可以比基线更宽**。
3. **票据 37** —— 冻结的原始证据。

**票据 37 不加指针，也不修改。**它被 `REQ-0146`／`REQ-0147`／`REQ-0148`／`REQ-0149` 四条的
Evidence 段落按 `Snapshot SHA-256 = 205a1c4bd5760e3f5a50de3eec050d5d8e99eec4ab74a789e005c22bf4a9c664`
绑定（git blob 口径，实测吻合），改动一个字符就让四条基线条目的证据指纹失效，那是一次需要走
变更提案流程的动作。**它因此事实上不可编辑**——「不留第二份可编辑的真相」这个目标由哈希绑定
达成，而不是由在它头上加一行指针达成。

## 一、获批调用清单

表格列的含义：**基线载体**是这一行的授权出处；**Facade** 是 `RIoT.Sdk.Facade` 里对应的具名
方法，`—` 表示获批但尚未实现；**用**列标注 `8005-agv-control-server` 的 `src/` 当前是否调用。

### 1.1 观察与计算

载体 `REQ-0146`（`v1.1.0` 修订，`CP-0001` 增列末尾五个 `imap` 端点）。使用 POST 的查询按无
副作用语义管理。

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| GET | `/api/version/v1/infos` | — | 否 |
| GET | `/api/task/v1/task/getVehicleInfo/{deviceKey}` | `GetVehicleExecutionFactsAsync` | **是** |
| GET | `/api/task/v1/route/` | `ReadDynamicRouteCostPresenceAsync` | **是** |
| GET | `/api/task/v1/route/curRemainCost/{orderKey}` | — | 否 |
| GET | `/api/task/v1/route/getCostUnit` | — | 否 |
| POST | `/api/task/v1/route/getRouteCostsBy` | `GetRouteCostAsync` | **是** |
| POST | `/api/task/v1/route/queryNearEnd` | `QueryNearestEndAsync` | 否 |
| POST | `/api/task/v1/route/queryNearestStart` | `QueryNearestStartAsync` | 否 |
| POST | `/api/task/v1/order/route/{vehicleKey}` | — | 否 |
| GET | `/api/imap/v1/mapInfo/edges/{mapId}` | `ListEdgesAsync` | **是** |
| GET | `/api/imap/v1/mapInfo/stations/{mapId}` | `ListStationsAsync`／`ListStationsStrictAsync`／`ListStationDetailsAsync` | **是** |
| GET | `/api/imap/v1/mapResource/removedEdge/{mapId}` | `ListRemovedEdgesAsync` | **是** |
| GET | `/api/imap/v1/mapResource/removedStation/{mapId}` | `ListRemovedStationsAsync` | **是** |
| GET | `/api/imap/v1/mapEdgeGroup/all` | `ListEdgeGroupsAsync` | **是** |

`REQ-0146` 在这十四条之外还写着一句「具体包括**当前 Facade 使用的地图/车辆/订单查询**」。
**那句话在这里展开成下面四条，展开即封闭**：

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| GET | `/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson` | `ListMapsAsync` | 否 |
| GET | `/api/task/vehicles/getAllVehicleSimpleInfo` | `GetDispatchableVehiclesAsync` | 否 |
| GET | `/api/task/vehicles/getVehicleInfoByDeviceKey` | `GetVehicleCardAsync` | **是** |
| GET | `/api/order/v1/orderRecord/detailByUpperId/{upperId}` | `GetOrderByUpperIdAsync`／`FindOrderByUpperIdAsync` | **是** |
| GET | `/api/order/v1/orderRecord/detailByOrderId/{orderId}` | `GetOrderByOrderIdAsync` | 否 |
| GET | `/api/order/v1/orderRecord`（按状态分页） | `ListOrdersByStatesAsync` | **是** |

展开的口径与证据，因为这一步是本文档唯一可能被读成扩权的地方：

- **时点是 2026-08-24**，即基线 `v1.0.0` 批准日。当时 vendored 的 `RIoT.Sdk.Facade` 恰好有
  **38 个 `*Async` 方法**（`0.1.0-controlserver.2`，元数据实测 38 个，与规格 9.2 记的数字一
  致），上表六条全在其中。**没有一条是后加的。**
- **展开按类别收窄，不按「Facade 里所有查询」。**那句话的三个词是「地图/车辆/订单」，所以
  `ListDevicesAsync`（`GET /api/device/v1/devices`，设备全量清单）与
  `GetDeviceStatusStatisticsAsync`（`GET /api/device/v1/devices/statistics/status`）
  **不在展开范围内**——`CONTEXT.md` 的 `ObservationAndComputationCall` 词条明写「设备全量清
  单、日志、配置、原始物模型和管理查询不属于本层」。Facade 里有它们，白名单不批它们。
- **展开之后这句开放引用作废。**再有新的 Facade 查询方法出现，它不因为「在 Facade 里」而获
  批，要进这张表得走第五节的变更流程。这正是把白名单从一份文档变成可判定清单的意义：
  **一个安全边界不能靠「当前」这个词的时点歧义承载扩权。**

### 1.2 建单：两种形态，不是一种

载体 `REQ-0147`（端点与前置条件）、`REQ-0294`（空闲返回只用单段 move）。

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| POST | `/api/order/v1/add/byDefaultMissions` | `CreateMoveOrderAsync`（只建得出形态一） | **是** |

端点只有一个，**订单体有两种获批形态**：

| 形态 | 用途 | `Mission` 数组 |
| --- | --- | --- |
| 一、单段 move | 普通搬运、空闲返回 | `[{ Type: "move", 目标站点 }]` |
| 二、move ＋ act | **前往充电桩** | `[{ Type: "move", 目标桩 }, { Type: "act", actionId: 78, actionParam1: 1, actionParam2: 0 }]` |

形态二的实测依据：`actionId=78` 是 RIoT 里现成的模板动作，`actionParam1=1` 为「开始充电」、
`=2` 为「结束充电」，`actionParam2` 均为 0（Round 24，
`rcs/riot-behavior-lab/evidence/rounds/2026-07-21-round-24/runs/PROBE-action78.json`，两条模板
均 2023-08-30 由 admin 建；分析见 `hypotheses/open-questions.md` 的 Q-033）。

**离桩不是 8005 的动作。**下一张订单到达队首时，RIoT 自动插入 `act(78, 2, 0)`
（Round 25 `execution-log.md:37`）。8005 没有、也不需要单独的「离桩」调用。

两条约束跟着形态二：

- **`CreateMoveOrderAsync` 建不出形态二。**它内部写死单元素 `Mission` 数组与 `Type = "move"`，
  没有 act 参数也没有重载。要建充电订单必须先扩这个 Facade 方法——**不是**改用 `.Raw`。
- **目标桩必须来自人工录入的充电桩名册。**RIoT 侧没有任何字段能标识充电桩，角色不能由站点名
  称、坐标或现场习惯猜测（`StationOperationalRole`）。录错一个 id 的后果是车开到普通工作站执
  行 `act(78,1,0)`，结果是 HANG + 407802。

不论哪种形态，`REQ-0147` 的前置条件不变：稳定 `upperId`、车辆 `ON_LINE` 已核验、`RouteCost`
可达、该车未占用 RIoT 单车订单名额。**模板建单、订单组合、改单不获批。**

### 1.3 受控订单命令

载体 `REQ-0148`。

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| POST | `/api/task/v1/order/command/{orderId}` | `PostOrderCommandAsync`（私有），经 `CancelOrderAsync`／`OrderHoldAsync`／`OrderContinueAsync`／`HangContinueAsync` | 否 |

**只有四个 `commandType` 获批**，其余一律不批：

| 命令 | 使用条件 |
| --- | --- |
| `CMD_ORDER_CANCEL` | 仅作用于 8005 自己创建且可关联 `TransportDemand` 的订单 |
| `CMD_ORDER_HELD` | 已批准保护条件下可自动触发 |
| `CMD_ORDER_CONTINUE_FROM_HELD` | 暂停原因消除 ＋ 重连/未结操作对账完成 ＋ 重新通过 `PreDepartureSafetyCheck` ＋ 服务端生成本次明确授权，四者齐备 |
| `CMD_ORDER_CONTINUE_FROM_HANG` | 只用于已批准原因白名单中的普通 HANG，受次数上限约束；**未知原因和充电失败不使用** |

### 1.4 调度可用性

载体 `REQ-0149`。

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| POST | `/api/task/vehicles/updateVehicleIntegrationLevel` | `UpdateIntegrationLevelAsync`，经 `DispatchEnableAsync`／`DispatchDisableAsync` | 否 |

`serviceId=enable|disable`。**它只决定车辆是否承接后续调度**：`disable` 不阻止建单进入
`QUEUEING`，不暂停也不终止当前 `EXECUTING` 订单。建单前必须独立确认 `ON_LINE`，调用后必须回查
实际状态。

### 1.5 条件式软件急停

**无基线载体，见第三节。**行为约束的载体是 `REQ-0246`／`REQ-0247`／`REQ-0248`／`REQ-0356`，但它们只写
动作名 `triggerEmergency`／`cancelEmergency`，不写路径；路径只在票据 37 里。

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| POST | `/api/device/v1/command/sync/service/{deviceKey}/{serviceId}` | `PostEmergencyServiceAsync`（私有），经 `TriggerEmergencyStopAsync`／`CancelEmergencyStopAsync` | 否 |

`serviceId ∈ {triggerEmergency, cancelEmergency}`，**只按以下规则使用**：

- **触发**：车辆在仓门未安全锁闭时移动、无 RIoT 订单可供 `OrderHold`、且无其它获批 RIoT 停车
  动作时，8005 自动 `triggerEmergency` 并进入持续保持。外部系统提前解除而仓门仍不安全时立即
  重触发并告警。**不得以 Cancel 代替停车**（`REQ-0246`）。
- **解除**：只由服务端调用 `cancelEmergency`，只从 `CAN_RECOVER` 解除；**`CAN_NOT_RECOVER` 时禁止调用**，
  转 RIoT 人员处理。解除后必须回查 `emergencyState=OK`，未回查到不算解除。急停状态经回查确认为
  `CAN_RECOVER`／`CAN_NOT_RECOVER` 即视为车辆已停稳（`REQ-0247`）。两条路径：
  - **自动解除**（`REQ-0167`）：8005 自己触发的急停，原原因消除且全部安全条件通过。
  - **人工确认解除**（`REQ-0356`）：服务端已登录且具有车辆查看权限的人员，对明确选中的车辆确认急停原因
    已消除、车上无货且全部仓门已关闭，并记下身份；该车仍有未进入终态的订单时不得调用，须先取消该订单。
    车载端不能确认，不要求二次认证、审批或第二人确认。这样解除的不属于原因消除前的意外恢复，不重触发
    （`REQ-0248`）。人员确认「仓门已关闭」只用于解除急停，不构成移动所需的锁闭证明（`REQ-0244`）。

### 1.6 鉴权面

载体：票据 37 的「鉴权与权限治理」节；`CONTEXT.md` 的 `RIoTCallAuthorizationGovernance` 词条。

| 方法 | 路径 | Facade | 用 |
| --- | --- | --- | --- |
| POST | `/api/auth/v1/admin/login` | `RiotSession.LoginAsync` | 否 |
| POST | `/api/auth/v1/admin/refreshToken` | `RiotSession.RefreshTokenAsync` | 否 |

**默认凭证是 CallApiKey**（目标环境独立，Bearer）。`AdminLogin`／`AccessToken` **只作人工应急
备用**，正常运行不走这条路。`OnboardHmi` 不持有任何 RIoT 凭证。

CallApiKey 原值必须存在于 ControlServer 的部署密钥存储，**不得**复制到 Git、普通配置、数据
库、日志、审计或业务响应；审计只记指纹。轮换靠人工判断，不设自动规则。

## 二、明确不批

白名单是**封闭**的：不在第一节表里的一律不获批，下面这些是曾被点名问过、答案是「不」的：

| 不批的 | 备注 |
| --- | --- |
| `DELETE /api/task/v1/route/dynamicRouteCost` | 「两个清除动态路由代价的 `DELETE`」之一，`REQ-0146` 明确排除 |
| `DELETE /api/task/v1/route/dynamicRouteCostByVehicle` | 同上；有副作用 |
| `PriorityExec` | Facade 里有 `PriorityExecAsync`（`POST /api/order/v1/orderRecordPriorityExec`），**只保留为未来候选** |
| 设备全量清单与状态统计 | `ListDevicesAsync`（`GET /api/device/v1/devices`）、`GetDeviceStatusStatisticsAsync`（`GET /api/device/v1/devices/statistics/status`）在 Facade 里，不在本层 |
| `POST /api/order/v1/add/byStaticTemplate`／`byDynamicTemplate`／`byOrderGroup` | 模板建单与订单组合；建单只批 `byDefaultMissions` |
| `POST /api/order/v1/orderRecordChangeVehicle` | 改单 |
| `interrupt`、`JUMP_FROM_HANG`、`REJECTED` | 订单命令只批 1.3 那四个 |
| 一键停靠、`parkConfig`、放行、指定充电、定位开始/停止 | `REQ-0294` 明确：生成了客户端不构成授权 |
| 地图/路线资源/车辆/模板/配置修改 | 任何写地图资源的调用 |
| 物模型任意写入、系统管理、清理、上传 | |
| `RawEscape`／`SendRawAsync`／任意 URL | 见下 |
| 生成客户端直接调用 | 重新生成 SDK **不自动扩权** |

**产品代码必须走具名 Facade，不得使用 `.Raw`。** 这一条有两个需求载体：

- `REQ-0309`：「实现只能经已批准的 RIoT Map/Station 只读调用面和**具名 Facade** 获取目录」
- 票据 37：「生产业务代码只可经具名 Facade 调用，默认拒绝 `RawEscape` 和任意 URL」

`REQ-0309` 是更稳的那一个——它是批准过的基线条目，而票据 37 是证据。**引用时优先引
`REQ-0309`。** 该限制只约束「谁可以调 RIoT」，不限制 ControlServer 连接车载端、数据库或其它
已批准依赖。

8005 只通过获批 RIoT API 控车，**不接入单机控制面，也不绕过 RIoT 发送 Modbus 或其它底层车辆
指令**。

## 三、三处如实登记的缺口

汇编过程中查出的三处，**都不在本文档的权限范围内修复**，登记在此以免下一个人重新撞上。

### 3.1 急停端点没有基线载体

1.5 那个路径只在票据 37 里，基线的 `REQ-0246`／`REQ-0248` 只写 `triggerEmergency` 这个动作
名。**这是白名单里唯一一条没有基线载体的获批调用**，而它恰好是后果最重的那一条。

处置建议：下一次变更提案（`CP-0002` 或之后）把路径补进一条基线条目。本文档不能代替那个动
作——汇编不产生授权。`CP-0003` 修订了急停的判停与解除，用户 2026-09-15 定该提案不补路径（`CP-0003`
第三节），缺口仍在。

### 3.2 基线里有一个白名单没列的端点

`REQ-0168` 写着「用户指定 `GET /api/task/vehicles/queryVehicleNotAssignOrder/{deviceKey}/{orderKey}`
作为长期 `QUEUEING` 原因诊断」。它**不在票据 37 的封闭清单里**，而票据 37 说「白名单到此封
闭」。

读法：`REQ-0168` 是**更晚的、用户指定的**授权，与票据 37 不冲突而是补充。它已列入第一节？
**没有**——本文档不替基线做这个判断。要用它，先走第五节的流程把它显式加进 1.1。

在那之前：**产品代码调用它就是越界**，票 08 的架构测试会因此变红，而那正是应有的行为。

### 3.3 五个获批端点没有 Facade

`GET /api/version/v1/infos`、`GET /api/task/v1/route/curRemainCost/{orderKey}`、
`GET /api/task/v1/route/getCostUnit`、`POST /api/task/v1/order/route/{vehicleKey}`，加上
3.2 的诊断端点。**获批不等于已实现**，要用它们得先在 `riot-sdk` 里加具名 Facade，不是用
`.Raw` 绕过去。

## 四、服务端当前实际调用

`8005-agv-control-server` 的 `src/` 在 2026-09-08 实际调用**十二个不同端点**，全部经具名
Facade，全部在第一节的表里：

| 调用点 | Facade 方法 |
| --- | --- |
| `Adapters/HttpRiotMovementGateway.cs` | `CreateMoveOrderAsync`、`FindOrderByUpperIdAsync`、`ListOrdersByStatesAsync`、`GetVehicleCardAsync`、`GetVehicleExecutionFactsAsync`、`ListStationsStrictAsync` |
| `Adapters/HttpRiotRouteCostProbe.cs` | `GetRouteCostAsync` |
| `Adapters/HttpRouteGraphSource.cs` | `ListEdgesAsync`、`ListStationDetailsAsync`、`ListRemovedEdgesAsync`、`ListRemovedStationsAsync`、`ListEdgeGroupsAsync`、`ReadDynamicRouteCostPresenceAsync` |

`.Raw` 在 `src/` 下**零命中**。注意 `src/` 里有三处**注释**提到 `.Raw`（`RouteGraphPorts.cs`、
`HttpRiotRouteCostProbe.cs`、`HttpRouteGraphSource.cs`，都在说「不要用它」）——**做机器守卫
的人当心**：朴素的文本 grep 会把这三处报成违规，判定必须在语法层面进行。

这一节是票 08（RIoT 白名单架构测试）的断言目标：`src/` 下 `.Raw` 零命中，且实际调用集合 ⊆
第一节的清单。**本文档是那条测试唯一的清单来源**，测试里不得出现第二份手抄清单。

### 给机器读的解析约定

第一节的表格就是清单，没有另一份数据文件——两份会不一致。解析规则，改本文档时必须守住：

- **获批的一行**：Markdown 表格行，**第一格恰好是一个 HTTP 方法**（`GET`／`POST`／`PUT`／
  `DELETE`／`PATCH`，无其它字符），第二格是反引号包裹的路径。第二节「明确不批」的行第一格是
  自由文本（如 `DELETE /api/…` 连写、或一句描述），所以两者不会混淆。
- **路径里的 `{…}`** 是路径参数占位符，匹配时按占位符处理而非字面量。
- 第二格出现 `?` 时（例如 `/api/order/v1/orderRecord`（按状态分页）），**查询串不属于路径**，
  匹配只比到 `?` 之前。
- 一行可以对应多个 Facade 方法（第三格用 `／` 分隔），因为多个具名方法可以打同一个端点。
- **不要把获批端点写进第一节以外的表格**，也不要在第一节里放不获批的行。清单的范围就是「第一
  节里第一格是 HTTP 方法的所有行」。

## 五、变更流程

| 变更方向 | 谁能做 | 怎么做 |
| --- | --- | --- |
| **扩大**（加端点、加订单形态、放宽条件） | 只有批准人 | 走需求基线变更提案（`requirements/change-proposals/`），基线先改，本文档后跟 |
| **收紧或暂停** | 软件负责人或实施负责人可**立即**执行 | 先执行后补记；恢复需要批准人明确批准 |
| **纠正汇编错误**（本文档写错了基线的内容） | 维护者 | 直接改，在提交信息里说明与哪条基线对齐 |

三条硬规则：

1. **本文档不产生授权。**它比基线宽的地方是错误，不是扩权。
2. **SDK 更新不自动扩权。**`riot-sdk` 新增一个 Facade 方法不意味着那个端点获批。
3. **运行时 build 或接口契约变化时**：观察调用继续用于识别和诊断，普通写操作暂停；危险状态中
   的 `OrderHold`／`triggerEmergency` 仍可尝试并回查。恢复其它写操作需要人工兼容性判断 ＋ 批
   准人批准。连接到错误环境时，除环境识别外全部禁止。

## 六、来源与追溯

| 来源 | 内容 |
| --- | --- |
| `requirements/baselines/current-requirements-v1.1.0.md` | `REQ-0146`／`REQ-0147`／`REQ-0148`／`REQ-0149`（第一节主体）、`REQ-0294`（空闲返回）、`REQ-0309`（具名 Facade）、`REQ-0246`／`REQ-0248`（急停行为）、`REQ-0168`（诊断端点） |
| `requirements/change-proposals/CP-0001.md` | 1.1 末尾五个 `imap` 端点的增列依据 |
| `requirements/change-proposals/CP-0003.md` | 1.5「解除」按修订后的 `REQ-0247`／`REQ-0248` 与新增的 `REQ-0356` 跟改的依据（需求基线 `v1.3.0`） |
| [票据 37](../.scratch/current-requirements-baseline/issues/37-decide-riot-api-allowlist-and-call-safety-boundary.md) | 冻结证据；1.5 与 1.6 的部分内容目前只有这一个来源 |
| `.scratch/8005-full-product/issues/04-answer.md` 第 1.6 节 | 充电订单形态二 |
| `rcs/riot-behavior-lab/` Round 24／25 | `act(78,…)` 的实测响应与自动插入行为 |
| `CONTEXT.md` | `ObservationAndComputationCall`、`RIoTCallDefaultDeny`、`RIoTCallAuthorizationGovernance`、`RIoTCallAudit`、`RawEscape` 词条 |

调用结果的处理规则（回查、重试、超时对账、审计记录范围）不在本文档，见票据 37 的「结果确认、
错误与重试」与「审计」两节，以及 `CONTEXT.md` 的 `RIoTCallAudit` 词条。
