# WIRE_TO_GATE 端到端闭环在发布候选上复跑通过（`81cb9cf`，generation 7）

## 运行类型

`TICKET14_FIELD_CLOSED_LOOP_ON_RELEASE_CANDIDATE`。用户在现场给出物理安全 GO 与逐次建单授权，
操作员身份 `S0020310`。四次绑定运行，每次全新 SQLite 与 journal，全部由发布候选
`w2g-rc-20260830-81cb9cf` 的二进制运行。

## 结论

**generation 7 全程走通：受理 → 建单 → 取货移动 → 到站 → 子批录入 → 两仓装货 → 发车安全检查 →
`TO_GATE` 移动 → 关卡到站 → 关卡批量卸货 → 原子完成。** 旅程终态 `Stage = Completed`，
`BlockReasonCode` 为空，`SessionGeneration` 全程停在 1，全程 5 分 42 秒
（`11:20:52.211Z → 11:26:34.333Z`）。

2026-08-29 的闭环走通绑的是 `3d8b00c`。其后 `127b137`（跨趟 worklist revision）、`1fd23ac`
（发布扫描闸门）、`d243abf`、`264615a`、`81cb9cf` 五个提交未在现场复跑过，本次补上，且部署形态
从开发默认换成 RC 的生产形态。

## 绑定身份

| 组件 | 版本 |
| --- | --- |
| ControlServer | `ControlServer_MVP@81cb9cf60a7990a7a7fb1b235042df5d0a9afd99`（发布候选自带包） |
| OnboardHmi | `OnboardHmi_MVP@304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6`（发布候选自带包，配置由 `appsettings.Production.template.json` 整体替换） |
| slots-simulator | `main@fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| 协议 | `protocol-v0.1.1` |
| MesIngest | 本机 `http://127.0.0.1:5088` |
| RIoT | `http://172.19.206.222:8888` |

车辆 `老厂前线新多仓位1` = `BROKERX-0c20ff0600d644869a6a80c186065d85`，地图 25。Demand
`ee57c677-9663-4806-8028-f474dec4434d`（`Q26086065-3|WIRE_TO_GATE`），取货站 `N6-8_N7-8`（56），
关卡 210，两筐落 1、2 号仓。

## 与 2026-08-29 那次的部署差异

本次是 RC 的**生产形态**，不是开发默认：

- 车载端配置从 `appsettings.Production.template.json` 整体替换，`environment=Production`；
- `wireToGate.useTls=true`，钉住服务端证书 SHA-256；
- `OnboardSafetyProjection` 启用，车载端经 HTTPS 读服务端的 RIoT 车辆停稳投影；
- 服务端 `Health:url` 为 HTTPS，`OnboardTransport.allowInsecureLoopback=false`。

由此产生一条 2026-08-29 那次看不到的行为，本次证实为**安全闸门按设计工作、且不会卡死旅程**：
车辆移动期间安全投影不满足停稳，车载端 readiness 落到
`RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`，服务端以 `ONBOARD_SESSION_NOT_READY` 挂起推进；
车一停稳，readiness 自动回到 `Ready / READY`，旅程继续。取货到站与关卡到站两次都是这个形态。

## 建单

两条真单，各一次成功，五步审计齐全：

| upperId | 用途 | 目标 | RIoT 订单 |
| --- | --- | --- | --- |
| `W2G-ee57c677-…-PICKUP-7` | `TO_PICKUP` | `N6-8_N7-8` / 56 | `order-2094022539885019136` |
| `W2G-ee57c677-…-GATE-7` | `TO_GATE` | `关卡` / 210 | `order-2094022957943881728` |

两条的审计序列都是
`PRE_CREATE_RECONCILIATION(UNKNOWN/AbsentAtObservation) → CREATE_DISPATCH(ARMED) →
CREATE_REQUEST(STARTED) → CREATE_RESPONSE(ACCEPTED/SdkAccepted) →
POST_CREATE_RECONCILIATION(CONFIRMED/Found)`，共 10 条 `RiotDispatchAuditEvents`，
`OrderIntents` 两行均 `CONFIRMED`。

终态计数：`AcceptedDemands=1`、`JourneyRuntimes=1`、`OrderIntents=2`、
`RiotDispatchAuditEvents=10`、`StationOperations=2`（`Load` 与 `Unload` 均 `Committed`）、
`OperationResults=2`、`ProtocolInbox=155`、`ProtocolOutbox=10`。`hostStderrEmpty=true`，
四端口全部回收，临时根证书移除后 `trustRemaining=0`。

## 前三次为什么没走通：都是本目录脚手架的缺陷，不是产品

产品在四次运行里的行为一致且正确。失败全部出在 `Invoke-SimulatorLoadAssist.ps1` ——
它替代操作员在仓门前放货/取货并关门，而 `workflow.operationTimeoutMs` 只有 120 秒、模拟器
`maxOpenDoors=1` 一次只开一个仓。

| 运行 | 到达 `Stage` | 失败原因 |
| --- | --- | --- |
| gen4 | `Blocked` / `LOAD_RESULT_REQUIRES_RECOVERY` | 当时还没有助手，人工关门时已超过 120 秒 |
| gen5 | `Blocked` / `LOAD_RESULT_REQUIRES_RECOVERY` | 助手在 `cargo` 之后对空响应做 `ConvertFrom-Json` 抛异常直接退出，`close-door` 从未发出；手动补关时是第 119 秒，2 筐里的第 2 仓根本没轮到 |
| gen6 | `Blocked` / `LOAD_RESULT_REQUIRES_RECOVERY` | 助手每轮都从当前货物状态重新推断意图，一次关门解析失败就把货物在 `OCCUPIED`/`EMPTY` 之间来回翻，2 号仓最终以 `EMPTY` 关门 |
| gen7 | `Completed` | 助手改为**开门时只决定一次目标状态**，之后只重试关门 |

gen7 的助手日志自证了这个修复：19:26:30 对 2 号仓的 `cargo` 命令连续三次响应无法解析，助手按计划
重试并在下一轮成功，正是 gen5 的致命失败形态。装货 1 号仓 2.5 秒、2 号仓 1.2 秒。

## 顺带证实的产品行为

- 装货失败留下的 `StationOperations.Status = RecoveryRequired` 之后，服务端持续重发同一条
  `SlotOperationCommand`，车载端按幂等规则回放原 `OperationResult`（日志：
  「忽略重复SlotOperationCommand…保留原OperationResult重放」），旅程停在 `Blocked` 不再前进。
  这在协议层是正确的，但**当前发布候选没有可用的操作员恢复出口**，见下节。
- `dispatchGeneration` 换代即得到新的 `upperId`，`PRE_CREATE_RECONCILIATION` 每次都是
  `AbsentAtObservation`，未出现重复建单。

## 路由到只读仓的发现

Owning repository: https://github.com/trytoreachpeak0/8005-agv-onboard-hmi

Routing status: read-only for agents；本仓不写入该项目内容

Impact on this ticket: 生产形态下车载端 HMI 的状态机与操作记录不反映 WIRE_TO_GATE 业务，
`RecoveryRequired` 没有操作员出口。闭环本身不受影响（gen7 已 `Completed`），但可用性验收要按
具名缺陷记录。

`OnboardController.ReevaluateIdleState` 要求 `_ruleGateway.IsConnected`；WIRE_TO_GATE 启用时
`App.xaml.cs` 注入 `DisabledRuleGateway`，其 `IsConnected` 恒为 `false`。因此在生产形态下：

- 顶部横幅永久停在「连接中 正在等待仓门控制设备和任务系统连接…」，尽管「上层会话：就绪」、
  「仓门控制：在线」、「发车安全：允许发车」、「到站：N1-14 / Q26085222-10」都正确显示；
- 「操作记录」只有启动两行，子批请求、装货命令、装货失败都不进 UI；
- 仓位卡进不了 `OperationStage.WaitingOperatorRecovery`，所以「重新打开 / 取消」按钮不出现；
- 服务端侧的异常恢复动作（`RESUME_AFTER_REPAIR`、`COMPENSATE_LOAD_ALL_EMPTY`、
  `FORCED_MECHANICAL_RECOVERY`）在 `WireToGateBusinessService` 中以
  `WireToGateRecoverySafetyFacts.Unknown` 求值而被安全策略一律阻断，该文件注释写明这是
  "Unknown recovery commands are intentionally logged and left blocked"。

合起来的后果：一旦某次站点操作落进 `RecoveryRequired`，现场操作员在随包 HMI 上没有任何前进或
撤销的手段。gen4 的截图是这一状态的现场证据。

## 边界

- 八仓 IO 由模拟器提供，**不构成**真实 IO 模块、接线、锁或光幕的资格；
- 车载端运行在开发工作站，不是目标车载终端，屏幕/触摸/扫码枪未取证；
- 本次未经过 `Install-ControlServerLocal.ps1` 的服务安装路径（需要管理员），因此也没有手册第 7 节
  描述的 NDJSON 持久日志；
- W2G-IS-00～07 的切片门禁状态不由本次现场运行改变。
