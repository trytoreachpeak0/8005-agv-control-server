# 零 mutation 受理演练：真实 MesIngest + 真实 RIoT，建单开关关闭

## 运行类型

`ZERO_MUTATION_REHEARSAL_NOT_G3`。本次不是任何切片的 G3，只验证在建单开关关闭的前提下，
运行时能否对真实外部事实做出正确的受理判定，且全程对 RIoT 零写入。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@1a0158c87c36fb2e3e0f4ddca1f7ed9c84d5672b`
- package manifest SHA-256：`3475c33e1962eafb0e076afb94bea496af8730a0551dd990c1fe822005f84c4b`
- MesIngest：本机 `http://127.0.0.1:5088`，contract `2026.08.new-mes-ingest.v2.4`，
  historyEpoch `104b2bed-3adf-47cf-a978-548a9e63b16f`，catalogRevision `2443`，202 条
- RIoT：`http://172.19.206.222:8888`，`Authorization: Bearer` 长期调用密钥（BC-AUTH-002）
- 隔离运行：全新 SQLite、临时端口 58105／58107、`--environment Development`

凭据只存在于本次子进程环境变量中，未写入配置文件、日志、证据或 Git。

## 有效态

`JourneyRuntime:enabled=true`、`RiotCreateDispatch:enabled=false`、
`RiotAbsentAtObservationCreateExperiment:enabled=false`、`OnboardSafetyProjection:enabled=false`。

已安装的生产服务未被停止、未被改动，本次运行使用独立端口与独立数据库。

## 身份回读（只读）

四项现场身份全部与已部署配置逐字一致：

| 项 | 配置值 | RIoT 只读回读 |
| --- | --- | --- |
| 车辆名 | `老厂前线新多仓位1` | 23 台中精确唯一命中 |
| vehicleKey | `BROKERX-0c20ff0600d644869a6a80c186065d85` | 同上解析结果一致 |
| mapId / mapIdentity | `25` / `老厂前线new` | 车辆运行属性 `mapName` 一致 |
| 关卡 stationId | `210` | 地图 25 的 206 个站点中 `210` 名称为 `关卡` |

车辆当时 `batteryPercentage=45`（高于已批准的 30% 阈值），`stationNo=210`，
`mapName=老厂前线new`。

## 受理判定结果

运行时读取完整目录并对 202 条 Demand 逐条判定，其中 WIRE_TO_GATE 15 条进入 backlog：

| ReasonCode | 条数 | 含义 |
| --- | --- | --- |
| `OUT_OF_SCOPE_AREA` | 8 | MES area 不在地图 25 的站点准入集内 |
| `ONBOARD_FACTS_NOT_READY` | 6 | 其余静态与动态门禁均通过，仅缺车载端仓位事实 |
| `PACKAGE_CAPACITY_NOT_UNIQUE` | 1 | 封装无唯一 boxes-per-basket 规则，已登记为 `MissingPackages` PENDING |

其余 187 条为 `OUT_OF_SCOPE_WORK_TYPE`（非 WIRE_TO_GATE），判定正确。

准入策略在启动时从地图 25 导入 205 条站点／任务类型关系，`AdmissionPolicyAudit` 内容哈希为
`646bb7c98559d8403733cb16a58923ced62813ac34580059a4ea369c8ddb3a47`，
deploymentId `MAP-25-WIRE_TO_GATE-20260827`。

原始 SUBLOT 与 PACKAGE 身份属于 MES 业务数据，按既有约定不进入本仓库，只保留聚合计数。

## 零 mutation 证明

- `AcceptedDemands`、`OrderIntents`、`RiotDispatchAuditEvents`、`JourneyRuntimes` 四表均为 0 行，
  因此不存在任何 `CREATE_REQUEST`、arm 或建单尝试；
- `HttpRiotMovementGateway` 的唯一写方法是 `CreateAsync`，它只能经
  `CreateAfterConfirmedAbsenceAsync` 或 `CreateAfterExperimentalAbsenceAsync` 到达，两者在
  开关关闭时于 arm 之前返回，故本运行不存在可达的 RIoT 写路径；
- 进程退出码经主动停止得到，stderr 为空，58105／58107 均已释放。

## 未证明的事项

6 条具备受理条件的 Demand 因缺少车载端事实未进入 `TO_PICKUP` intent，因此本次**没有**观察到
`CreateDispatchDisabled` 这一实际拒绝分支的运行态证据；该分支目前只有单元测试覆盖。要补上
这一段，必须接入真实 OnboardHmi 与 slots-simulator。

本次未启动 Onboard 或 simulator、未调用 RIoT mutation、未建单、未移动车辆。W2G-IS-00～07 的
正式 G3 与 RC 保持 `INCONCLUSIVE`。

## 附带发现

本机 `8005-agv-onboard-hmi` 检出停在 `a1e32dd`，落后已发布的
`OnboardHmi_MVP@84b7f3f66ff2f867b18121760f38e26e0bbd6fa5` 共 9 个提交（`a1e32dd` 是二者的
merge-base，非分叉）。其中包含 `Fix cold-start safety readiness recovery` 与
`Add end-to-end safety readiness regression`，正好覆盖本次遇到的车载安全就绪路径。因此该
检出上的既有 Release 产物不能用于绑定证据的联合运行；后续必须从一次性克隆按 `84b7f3f`
构建。该仓库对 agent 只读，本次未对其做任何修改。
