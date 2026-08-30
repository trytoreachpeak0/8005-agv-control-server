# 2026-08-30 带 demand 的 G3：结果重放与 RIoT UNKNOWN 对账

## 结论

`DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT` 运行 `20260830T000453998Z`
通过，十六条断言全部 PASS。本结果不是任一完整切片的正式 G3 PASS；`W2G-IS-04` 与 `W2G-IS-05`
仍保持 `INCONCLUSIVE`。

这两类向量是 staged 运行取不到的最后两个。**两者都不需要移动车辆。**

- **RIoT UNKNOWN 对账**不是偶发故障，而是每次新代次建单的必经路径：任何从未创建过的 `upperId`，
  RIoT 都以 HTTP 200／业务码 0／无 result 应答，落到 `RiotOrderObservationKind.Unknown`。要断言
  的不是「它偶尔发生」，而是服务端把它认定为精确的 absent-at-observation 并**只建一次单**，
  而不是确认一张自己没见过的订单。
- **`OperationResult` 首次被接受**只可能发生在「命令已下发、结果尚未到达」的站点操作上。
  `ApplyOperationResultAsync` 以 `ResultId` 或 `(SlotOperationAttemptId, ForcedRecoveryGeneration)`
  去重，已带结果的 attempt 只会产生冲突；而真实车载端会立刻把结果送回，所以**新跑一趟现场旅程
  反而取不到这一步**。本运行恢复了一份真实授权运行留下的该状态，而不是伪造 `StationOperations` 行。

本次没有创建 RIoT 订单，没有发送移动命令，没有使用现场凭据，也没有伪造停稳/驻车信号：
`JourneyRuntime` 关闭，`RiotCreateDispatch` 关闭，MesIngest 与 RIoT 均指向死端口
`http://127.0.0.1:1`，传输为明文回环，未安装任何临时根证书，全程无人值守。

## 冻结身份

- ControlServer：`3d8b00c7558ae700358f1f995a5ac75d12a3250c`
- OnboardHmi：`304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6`
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest SHA-256：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- runner：`ee48ca56d8516b4434eaad5a5811066dccc31b63`，起跑时工作区干净
- 运行配置 SHA-256：`0fa0be9f90060aab65a54d0f2eafde9e87526a07423f03b541193e7d7d7811cf`

四个 peer commit 与合成对端 harness 都不在本 runner 内重述：commit 解析
`scripts/run-staged-g3.ps1` 的 `param()` 块默认值读回（来源 SHA-256 `50a7a4d6a0b4…`），
读取函数本身按 AST 从 `scripts/run-staged-g3-restart.ps1` 抽出（SHA-256 `f798464d9ec3…`），
harness 则按 here-string 从同一个 staged runner 抽出后编译。三者都记录在 `configuration.json`。

运行中的服务端在 `SessionAccepted` 里自报 `serverBuildCommit = 3d8b00c…`，二进制由该 commit 的
干净精确克隆发布。

## 存储来源与其边界

存储恢复自授权现场运行 `fullloop-20260829T131549Z`（`AUTHORIZED_SINGLE_REAL_CREATE`），
数据库 SHA-256 `87220f7990990106940e50d5e9e9bf634bcfd841e41582d7c9692b66902db773`。
运行前基线：demand `94993971…` 已受理、车辆租约未释放、两条 `StationOperations`
（Load `bcf50fba…` 为 `Committed`、Unload `e2056294…` 为 `Prepared` 且无结果行）、
十条 `RiotDispatchAuditEvents`、`UnloadBatches`／`StopClosures`／`TransportDemandCompletions`
均为 0。

**边界要写明**：这份存储是真实 demand 产生的真实状态，但写它的构建早于 `3d8b00c`。
被测行为完全属于 `3d8b00c`，状态的来源不是。已记录在 `configuration.json` 的 `storeProvenance`。

## RIoT UNKNOWN 对账（对存储中真实审计链的断言）

两条腿各一条完整五阶段链，序号 1..5 无缺口：

| upperId | 1 | 2 | 3 | 4 | 5 |
| --- | --- | --- | --- | --- | --- |
| `…-PICKUP-2` | `PRE_CREATE_RECONCILIATION` / **UNKNOWN** | `CREATE_DISPATCH` / ARMED | `CREATE_REQUEST` / STARTED | `CREATE_RESPONSE` / ACCEPTED | `POST_CREATE_RECONCILIATION` / CONFIRMED |
| `…-GATE-2` | `PRE_CREATE_RECONCILIATION` / **UNKNOWN** | `CREATE_DISPATCH` / ARMED | `CREATE_REQUEST` / STARTED | `CREATE_RESPONSE` / ACCEPTED | `POST_CREATE_RECONCILIATION` / CONFIRMED |

- 两条 UNKNOWN 的 `eligibilityBasis` 均为 `ABSENT_AT_OBSERVATION_IDEMPOTENT_CREATE`，
  `resultPresent = 0` 且 `returnedOrderId` 为空——是精确的 absent-at-observation，
  不是传输失败。用后者去确认缺席会让幂等建单不安全。
- 每条腿只有一次 `CREATE_REQUEST`／`CREATE_RESPONSE`：UNKNOWN 没有诱发第二次建单。
- 最终确认的订单号与本次创建返回的订单号、以及 `OrderIntents.OrderId` 三者一致
  （`order-2093689119296323584` / `order-2093690819126099968`），`OrderIntents.Status` 为
  `CONFIRMED`——服务端信任的是它自己创建的那张单，不是从 UNKNOWN 推断出来的。

## `OperationResult` 结果重放（对真实已持久化行的现场驱动）

合成对端以 `老厂前线新多仓位1` 建立会话（generation 74 → 75），针对 `Prepared` 的
Unload attempt `e2056294…`：

| 断言 | 结果 |
| --- | --- |
| 首次结果被接受 | PASS，`DurableAck` 的 `acceptedMessageId` 等于发送的 `60e2187e…` |
| 逐字节相同的重放返回同一条已存 ack | PASS，两次 ack SHA-256 均为 `2874fb3ff627…` |
| 同 messageId 改内容被拒 | PASS，连接关闭 |
| 同 attempt 同代次换 messageId 被拒 | PASS，连接关闭 |
| 已 `Committed` 的 attempt 再收结果被拒 | PASS，连接关闭 |
| 过期 session generation 的结果被拒 | PASS，连接关闭 |

跨会话代次的重放**不在用例内，且是设计使然**：`OperationResult` 没有像 `RecoveryStateReport`
那样把 `sessionGeneration` 归零的重放身份哈希，所以同一 messageId 换代次重发必然内容哈希不同，
按冲突处理。四条拒绝分支各自新开连接，因为一次拒绝会关掉它所在的那条连接。

重放不是「又跑了一遍」：运行后 `e2056294…` 只有一条 `OperationResults` 行（`ResultId` 等于
`60e2187e…`、`historicalOnly = 0`），`ProtocolInbox` 中该 messageId 只有一行，站点操作恰好
一次转为 `Committed` 并写入证据。

卸货结果同时关闭了 demand，四项事实原子提交：`UnloadBatches` 0→1、`StopClosures` 0→1、
`TransportDemandCompletions` 0→1，车辆租约释放。这一段是 staged 运行永远到不了的。

## 无移动与无外部副作用

只有外部调用才能增长的表在运行前后计数完全相同：`OrderIntents` 2→2、
`RiotDispatchAuditEvents` 10→10、`AcceptedDemands` 1→1、`VehicleDispatchLeases` 1→1；
`StationOperations` 2→2，即没有任何行被伪造。`configuration.json` 中
`realRiotOrderCreated`、`movementCommandSent`、`stationOperationRowFabricated`、
`realExternalCredentialsUsed` 均为 false。监听端口 58305／58307 在运行结束后已释放，
凭据扫描无泄漏。

## 断言的可证伪性

正式运行前，runner 的断言段被原样抽出（按首行／停行从脚本中取真代码块，不是复刻品）并喂入
本次运行的真实产物，再施加 **27 条单点变异**，全部被其对应断言检出。变异覆盖：漏掉 UNKNOWN、
把 UNKNOWN 记成别的 eligibility basis、UNKNOWN 其实带了 result 或订单号、某条腿建单两次、
跳过建单后对账、确认的订单不是创建的那张、order intent 未 CONFIRMED、六条探针用例逐一翻红、
重放被处理成两条结果行或两条 inbox 行、结果被记为 historical-only、操作没离开 `Prepared`、
完工行未写或写重、运行中冒出新的 order intent／审计事件／站点操作行、端口未释放、凭据泄漏。

期间抓到两类缺陷，都不是产品 FAIL：

1. **`$x = if (...) { @(单元素) }` 会被 `if` 语句的输出流拆包**，`$x` 变成裸 `[ordered]`，
   `.Count` 返回的是键数而不是行数——`replayedResultWasNotProcessedTwice` 因此在数据库其实
   正确的情况下变红。改为在 `if` **语句**内赋值。这是 `,$rows` 那一类陷阱的新变体。
2. 一条变异按位置取 `operationResultRows[1]`，而该表按 `ReceivedAt` 排序、合成结果的
   `observedAt` 把它排到了前面，于是变异改到了另一条 attempt 上并「存活」。是变异写错，
   不是断言漏检；改为按 attempt id 选行后被检出。

## 证据

- [`run-result.json`](run-result.json)：机器可读运行结果，SHA-256
  `d02a15d1547b76bbc5905dde4f52bbc40785e22b84cf516e25a7c9fd6f3807e0`
- [`configuration.json`](configuration.json)：存储来源、绑定来源、端口、无副作用声明
- [`probe-result.json`](probe-result.json) / [`probe-transcript.ndjson`](probe-transcript.ndjson)：
  合成对端六条用例的请求／应答哈希与逐条记录
- `logs/`：克隆、检出、发布日志与服务端 stdout/stderr
- `run-result.json` 的 `evidenceFiles` 收录本目录其余文件的 `path/sha256/length`

## 仍未通过

- 本运行不覆盖真实车辆移动、现场停稳判据与端到端旅程时序；
- 存储来自早于 `3d8b00c` 的构建，见上文「存储来源与其边界」；
- `W2G-IS-04`／`W2G-IS-05` 的完整向量与现场验收均未宣称通过；
- 完整 G3 与 RC 仍为 `INCONCLUSIVE`。
