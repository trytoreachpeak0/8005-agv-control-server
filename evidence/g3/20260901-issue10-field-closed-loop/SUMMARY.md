# WIRE_TO_GATE 真车闭环在明文候选上走通（`19ce7db`，generation 8）

## 运行类型

`TICKET10_FIELD_CLOSED_LOOP_ON_PLAINTEXT_RELEASE_CANDIDATE`。用户 2026-09-01 在现场给出物理安全
GO 与本次建单授权，操作员身份 `S0020310`。一次绑定运行，全新 SQLite 与 journal，全部由发布候选
`w2g-rc-20260901b-19ce7db` 的二进制运行。这是本轮明文改造的验收运行，也是票 09 之后 map 上的
最后一张执行票。

## 结论

**generation 8 全程走通：受理 → 建单 → 取货移动 → 到站 → 子批录入 → 两仓装货 → 发车安全检查 →
`TO_GATE` 移动 → 关卡到站 → 关卡批量卸货 → 原子完成。** 旅程终态 `Stage = Completed`，
`BlockReasonCode` 为空，`SessionGeneration` 全程停在 1，全程 11 分 25 秒
（`02:59:48.614Z → 03:11:13.648Z`）。

传输层是明文：链路 A 是 NDJSON over 明文 TCP（`192.168.200.1:58705`），链路 B 是明文 HTTP
（`http://192.168.200.1:58707/api/onboard/v1/vehicle-safety`）。全程无证书、无信任存储写入、
无密钥材料文件。

## 绑定身份

| 组件 | 版本 |
| --- | --- |
| ControlServer | `ControlServer_MVP@19ce7db70893afea6c6361988c3bc612d77569d0`（发布候选自带包） |
| OnboardHmi | `OnboardHmi_MVP@238b46eb2c9ae90584e4288a782176f66b7de942`（候选自带包，配置由 `appsettings.Production.template.json` 整体替换） |
| slots-simulator | 本机 Release 构建，作为 Modbus IO 输入 |
| 协议 | `protocol-v0.1.1` |
| MesIngest | 本机 `http://127.0.0.1:5088`（服务 Running，PID 17628） |
| RIoT | `http://172.19.206.222:8888` |

车辆 `老厂前线新多仓位1` = `BROKERX-0c20ff0600d644869a6a80c186065d85`，地图 25。Demand
`f37a0950-4a3e-437c-a745-9dc5050ff07d`（`Q26084908-12|WIRE_TO_GATE`，AREA `N2-13`，EQP
`3QHS6015`，PACKAGE `TO-252-2L(4R)`），取货站 `N2-13_N3-13`（29），关卡 `关卡`（210），两筐落
1、2 号仓。

## 0. 开跑前预检：合格需求是查出来的，不是碰上的

票 09 把 `DEMAND-ACCEPTED` 记为 INCONCLUSIVE，理由是「运行时刻 MES 里没有一条需求满足现场条件」。
本票开跑前用 `Invoke-Ticket10Preflight.ps1` 先把这件事变成可查的：只做 GET（MesIngest 需求目录、
MesIngest `SUBLOT_BOX_COUNT`、RIoT `GET /api/imap/v1/mapInfo/stations/25`），按
`JourneyRuntimeEngine` 的静态闸门顺序逐条判定，不动库、不建单。

10:55 那一版快照（`catalogRevision=5966`）：15 条 `WIRE_TO_GATE` 里 3 条通过全部静态闸门；开跑前
（`5973`）复跑到 6 条。其余是 `OUT_OF_SCOPE_AREA`（area 不以 `N` 开头）11 条与
`AREA_STATION_NOT_FOUND`（N23-6 在 map 25 上无 AreaNamedMachineStation）1 条。

**对票 09 结论的更正**：票 09 那 12 条的分布是
`OUT_OF_SCOPE_AREA=10 + AREA_STATION_NOT_FOUND=1 + BATTERY_POLICY_NOT_SATISFIED=1`。
`BATTERY_POLICY_NOT_SATISFIED` 在引擎里排在 area、AREA_EQP 唯一性、站点解析、包装容量**之后**
（`JourneyRuntimeEngine.ValidateDynamicFacts`），所以那一刻**其实有一条需求通过了全部静态闸门**，
卡住它的是电量策略（`minimumBatteryPercent: 30`，或车辆处于 `CHARGING`），不是需求池空。
「MES 里长期取不到合格需求」这个担忧不成立，票 10 正文里「先具名排查现场条件」的预案没有触发。

顺带记一条 harness 事实：RIoT 的 `CallApiKey` 是按 **`Authorization: Bearer <key>`** 发的。
试 `CallApiKey`、`X-Call-Api-Key`、`apiKey` 三种同名请求头都回 401；SDK 程序集里只留了
`BearerPrefix` 这一处线索。

## 1. 闭环本身

`run-result.json` 的关键字段：

| 项 | 值 |
| --- | --- |
| `journeyStage` | `Completed` |
| `blockReasonCode` | 空 |
| `completed` | `True` |
| `ExpectedBasketCount` | 2 |
| `WorklistRevision` / `PlanRevision` / `VehicleBusinessRevision` | 1 / 1 / 1 |
| `SessionGeneration` | 1（唯一一行，全程未变） |
| `AcceptedDemands` | 1 |
| `StationOperations` / `OperationResults` | 2 / 2 |
| `ProtocolInbox` / `ProtocolOutbox` | 196 / 10 |

两条真单，各一次成功，五步审计齐全：

| upperId | 用途 | 目标 | RIoT 订单 |
| --- | --- | --- | --- |
| `W2G-f37a0950-…-PICKUP-8` | `TO_PICKUP` | `N2-13_N3-13` / 29 | `order-2094621232220733440` |
| `W2G-f37a0950-…-GATE-8` | `TO_GATE` | `关卡` / 210 | `order-2094623072786186240` |

两条的审计序列都是
`PRE_CREATE_RECONCILIATION(UNKNOWN/AbsentAtObservation) → CREATE_DISPATCH(ARMED) →
CREATE_REQUEST(STARTED) → CREATE_RESPONSE(ACCEPTED/SdkAccepted) →
POST_CREATE_RECONCILIATION(CONFIRMED/Found)`，`RiotDispatchAuditEvents` 共 10 条，无重复建单。

仓位作业两条都 `Committed`，`SublotId` 均为 `Q26084908-12`：

| OperationType | Status | SublotId |
| --- | --- | --- |
| `Load` | `Committed` | `Q26084908-12` |
| `Unload` | `Committed` | `Q26084908-12` |

## 2. 明文形态下的安全闸门：拦了两次，也放行了两次

这是本票相对票 14 的**必须重证项**——安全投影的传输方式变了，票 14 证过的「移动中拦、停稳放行」
不能直接继承。

观测到的形态与票 14 一致，且两条腿各出现一次：

- 取货腿：`10:59:59 → 11:03:12` 期间 `stage=AwaitingPickupArrival`、
  `block=ONBOARD_SESSION_NOT_READY`、`readiness=RecoveryRequired/DEPARTURE_SAFETY_NOT_READY`；
  车停稳后 `11:03:32` 自动回到 `Ready/READY`，旅程推进到 `AwaitingSublot`。
- 关卡腿：`11:07:27 → 11:10:48` 同样形态；`11:11:13` 回到 `Ready/READY` 且直接落到 `Completed`。

两次都是**自动**恢复，没有人工干预、没有卡死旅程。安全投影本身是明文 HTTP：服务端日志里
`HTTP GET /api/onboard/v1/vehicle-safety responded 200` 共 556 条（每秒一次，全部 200）。

## 3. 「本轮真的没有 TLS」——用运行自己没写过的观测

证据字段是我们自己写的字面量，不能自证。因此另取四类观测：

- **进程自报**。服务端启动行：`Onboard NDJSON listener started on 192.168.200.1:58705;
  transport=plaintext` 与 `Now listening on: http://192.168.200.1:58707`。车载端启动行：
  `车载端启动：agvId=老厂前线新多仓位1，environment=Production，version=0.2.0-safety-mock-travel+238b46eb…`。
  `environment=Production` 是唯一让 `IsForbiddenProductionHost` 真正生效的模式，绑定
  `192.168.200.1` 而非 loopback 是被守卫检过的事实，不是约定。
- **四个证书存储的排序指纹集合摘要跨运行零变动**（比数量更强，换一张同样会翻）：
  `CurrentUser\Root` 44 张、`CurrentUser\My` 1 张、`LocalMachine\Root` 42 张、
  `LocalMachine\My` 1 张，起止 digest 完全相同，`storeDrift = 0`。
- **run root 递归零密钥材料文件**（`.pfx/.pem/.cer/.crt/.key/.p12`），`keyMaterialFiles = 0`。
- **全部日志搜 `Schannel|SslStream|AuthenticationException|X509|certificate|https://` 命中 0**
  （27190 行服务端日志 ＋ 146 行车载端日志），`tlsDiagnosticHits = 0`。

另外，配置面在写入后被**回读**校验：车载端 `appsettings.json` 无 `REPLACE_` 占位符、无 `useTls`、
无 `serverCertificateSha256`、无 `serverCertificatePath`、无 `https://`；启动脚本还显式清除了
`OnboardTransport__serverCertificatePath`、`…__serverCertificatePasswordEnvironmentVariable`、
`…__allowInsecureLoopback`、`OnboardSafetyProjection__requireHttps` 四个环境变量注入点——
过时键校验比对的是**键名**，空值注入同样算键存在。

## 4. 红侧：同一份代码，四个检测器各双向

`Invoke-Ticket10RedSide.ps1` 把上面第 3 节里由 harness 承担的四个检测器各跑两次：一次在未改的
绿侧输入上（必须静默），一次在**单字段**变异上（必须响）。红侧执行的是
`Stop-Ticket10FieldRun.ps1 -DetectorsOnly` **同一个文件**，不是复刻实现；`-DetectorsOnly` 只跳过
杀进程那一段，检测器逐字未动。

| 检测器 | CONTROL | RED 变异 | 结果 |
| --- | --- | --- | --- |
| `store-digest-drift` | 0 | 换一张指纹、**数量不变** → 1 | PASS |
| `key-material-scan` | 0 | 种一个 `.pfx` → 1 | PASS |
| `tls-diagnostic-scan` | 0 | 加一行 `AuthenticationException` → 1 | PASS |
| `production-pid-guard` | `True` | 基线 PID 加一 → `False` | PASS |

**8/8 PASS。** 指纹那一条特意让数量保持不变——计数式检测器看不见这种变化，只放「该响的」一侧
是发现不了的。

## 5. 生产环境未受影响

生产服务 `8005 AGV ControlServer` 全程 **PID 8632** 未漂移，三个监听地址
（`::1:58007`、`127.0.0.1:58005`、`127.0.0.1:58007`）起止一致。监听按**服务 PID** 取，不按进程名
——隔离实例进程同名，按名取会把探针端口混进来。隔离实例用 58705/58707，与生产及历史 staged 端口
（58105/58205/58305/58425/58505/58605）均不冲突，收尾后 `portsReleased = True`。

车载端仓 `8005-agv-onboard-hmi` **写入仍为零**：本次用的是候选包里打好的车载端，身份取自
`release-manifest.json`，未克隆、未检出、未修改。

## 6. 据实记录：三条不能算进结论的东西

**(a) `HW-REAL-IO` 仍为 INCONCLUSIVE。** 用户选定 IO 输入为八仓模拟器（同票 14），
`Invoke-SimulatorLoadAssist.ps1` 代替门前操作员开合仓门。车载端日志里的
`已向物理N号仓发送开锁脉冲触发，DO地址=100/101` 与逐次 DO／锁 DI／光幕 DI 变化是真实的 Modbus
往返，但对端是模拟器。**真实 IO 模块、接线、锁与光幕的资格本票未取得**，按票 09 的具名外部资格
原样保留。

**(b) HMI 截图未采集。** 票 10 正文写「HMI 操作记录跟随业务节点（沿用票 29 的截图取证方式）」。
本次运行中 HMI 由用户在现场操作，未截图。业务节点的记录改由 `gen8/onboard.log` 承担：会话建立
（1 次）、子批录入请求（服务端轮询 100 余次，用户于 11:07 前录入）、两次装货与两次卸货的完整
IO 序列均在其中。**这是取证方式的偏离，不是等价替换**，如果票 11 或后续需要截图，须另行补采。

**(c) 一个先于本轮存在的产品缺陷，在闭环完成之后暴露。** 旅程 `11:11:13` 完成后，
`11:11:16 / :18 / :20` 三次轮询各抛一次
`ControlServer.Domain.BusinessIdentityConflictException: Accepted demand replay does not match its
original order intent.`，日志记为 `Journey runtime iteration failed closed; no stage is inferred
from memory.`。成因是该 demand 仍留在 MES 目录里，引擎再次走受理路径、按新的 `MovementLegId`
去找既有 `OrderIntent` 找不到（`WireToGateStore.cs:235`）。

**这不是本轮改造引入的**：调用链在 `WireToGateStore.AcceptCoreAsync` /
`DemandIntakeService.AcceptCoreAsync`，与传输层无关；同样的
`Journey runtime iteration failed closed` 在 TLS 期的票 14 gen7 完成之后也连续出现
（`evidence/g3/20260830-issue14-field-closed-loop/gen7/host.out.excerpt.log:340` 起）。
它 fail-closed、未污染本次结果，但会让运行期在完成后无法再受理**其他**合格需求。已具名，归属
另起一轮，不在本 map 的 Destination 内。

另外两条噪声，确认无害：`11:11:26` 的
`Onboard connection ended with a protocol or transport error.` ＋ `SocketException (10054)` 是
收尾脚本杀掉车载端进程所致，发生在完成之后；装货辅助日志里 3 行 `unparsable response` 是它自己
curl 解析的瞬时失败，已自重试成功，仓位终态正确。

## 7. 归档内容

| 文件 | 说明 |
| --- | --- |
| `Invoke-Ticket10Preflight.ps1` / `preflight-20260901T1055.txt` | 开跑前只读预检与一次留档输出 |
| `Start-Ticket10FieldRun.ps1` | 明文形态部署与启动（无证书、无信任存储写入） |
| `Watch-Ticket10FieldRun.ps1` | 只读观测（快照副本，不碰在跑的库） |
| `Stop-Ticket10FieldRun.ps1` | 收尾与取证；`-DetectorsOnly` 供红侧复用同一份检测器 |
| `Invoke-Ticket10RedSide.ps1` / `red-side.json` | 四检测器双向红侧，8/8 |
| `Invoke-SimulatorLoadAssist.ps1` | 门前操作员替身（自票 14 逐字复用） |
| `gen8/run-start.json` / `gen8/run-result.json` | 运行身份与全部断言 |
| `gen8/host.out.excerpt.log` | 服务端日志摘录；折叠了哪几类每秒轮询、各多少行，在文件头写明 |
| `gen8/onboard.log` | 车载端完整日志（146 行，未删减） |
| `gen8/simulator-assist.log` | 仓门辅助日志 |
| `gen8/host.err.log` | 空（0 字节） |

run root `C:\Users\szy\w2g-ticket10\`（含 169MB 的车载端部署副本与 SQLite）留在磁盘上，未清理，
等用户指示。
