# 批次9-13：充电投运参数、两份名册与开关窗规程（cs#411）

人工票 cs#411 的记录。本次（2026-10-03）只做票面第 1～3 步，再加配置草稿与 10-07 清单：

- 把策略数值的批准记下来；
- 准备演练策略文件；
- 用 cs#400 的 `--dry-run` 在**临时库**上静态校验四个文件；
- 写清桩出口的配置草稿；
- 写 10-07 清单。

**没有**在任何实例上导入，没有启动任何 ControlServer，没有碰 factory01、agv01、MVP、生产 MesIngest、RIoT 或任何车。
票面第 4 步（10-07 在并行实例上导入）与第 5 步起（每次开关窗）是用户的动作，每一项单独授权，结果按第八节追加进本文件。

| 文件 | 内容 |
| --- | --- |
| `checklist-1007.md` | 10-07 一页清单：导入什么、哪些开关关着及原因、现场核对、哪些步骤要逐次授权 |
| `catalog-26.json` | 26 号图站点目录（名册导入的 `--catalog`），出处见第四节 |
| `dry-run/` | 四个文件的 dry-run 输出、两个反例、临时库上的只读输出 |
| `config-drafts/` | `VehicleFaultRecovery` 与 `FieldOperatorRoles` 两节的草稿、R-11／R-13 名单模板，说明在其 `README.md` |

## 一、批准记录

| 项 | 内容 |
| --- | --- |
| 批准人 | 用户 Zhengyu Shao |
| 时间 | 2026-09-29 |
| 在哪里批准 | 调度会话（Coordinator 9）里，用户回答批次 9 方案的待定问题：「批次 9 第 6～11 全按推荐（…投运参数照方案表…）」 |
| 原话出处 | **转述**。原话在调度会话里，本会话读不到。可核的记录有三处：调度看板 `8005-workspace-v2/ds-tasks/board.md:1032`（09-29 用户条）；分票方案 `8005-workspace-v2/ticket-tools/batch9/plan.md` 第七节第 6 条（被批准的那张表，含 10-08 临时策略 50／62）；cs#411 票面第 1 步「用户 2026-09-29 定（经调度会话 Coordinator 9 转达）」。前两个文件在私有工作区里，不在本仓库 |
| 记录方式 | 调度 10-03 定：按「用户 09-29 在调度会话中批准（转述）」记 |

FieldOps 里的批准记录（`approve-charging-policy` 的 `--approved-by`、`--role`、`--basis`）要到 10-07 在实例上批准时才产生。`--role` 暂定「产品负责人」（调度 10-03 定），**导入前请用户确认这个值**。
`--basis` 建议写：「用户 2026-09-29 在调度会话中批准（转述），批次 9 方案第七节第 6 条；cs#411」。

## 二、正式版策略：`docs/field/charging-policy-map26.json`（cs#400 已入库，本 PR 不改）

| 字段 | 值 | 依据（方案第七节第 6 条；【读到】是从代码、文档、证据里实读到的，【推的】是据此推断的） |
| --- | --- | --- |
| `chargingCompletionThresholdPercent`（充满线） | 80 | MVP 生产一直用 80%，22→80 约 39 分钟（含开过去）【读到：cs#273】；再往上充得慢、占桩时间长【推的】 |
| `mandatoryChargeEntryThresholdPercent`（强制充电线） | 30 | MVP 出厂值 30%【读到】；生产覆盖成 22% 后，09-21 等人期间两次耗到车载机断电【读到：cs#273】；单桩排一辆车的队约掉到 24%，仍高于 15% 救命告警线【读到数据、推的换算】 |
| `minimumPostTaskBatteryMarginPercent`（最低任务后电量余量） | 20 | 与每趟耗电估计合起来，电量不低于 30% 才接单，正好等于强制充电线，不留「既不接单也不去充电」的空档【读到：MVP 09-12 缺陷第二层】 |
| `estimatedTaskConsumptionPercent`（每趟任务耗电估计） | 10 | 干活时约 10～12%／小时【读到数据：cs#273】；一趟通常不到一小时【推的，可用 10-08 实跑校准】 |
| `progressStabilizationSeconds`（无进展判断稳定期） | 180 | 覆盖 `act(78,1,0)` 到 RIoT 报 `CHARGING` 的延迟；这段延迟没测过【推的】 |
| `progressObservationWindowSeconds`（观察窗口） | 600 | 09-12 实测 18:23 到桩、18:31 从 49% 到 62%，至少约 1.7%／分钟【读到：`ControlServer_MVP` 分支 `evidence/field/20260912-FW-FL2-charging/SUMMARY.md`】 |
| `progressMinimumIncreasePercent`（最小电量增量） | 3 | 正常涨幅的约五分之一，给接近 80% 时变慢留余量【推的】 |
| `vehicleScope` | `[]`（全部投运车辆） | 见下 |

**硬关系代入**（`REQ-0281`：`ChargingCompletionThreshold > MandatoryChargeEntryThreshold >= 最低任务后电量余量`）：80 > 30 成立，30 ≥ 20 成立。
**与救命线的关系**（cs#403，评论 C3）：服务端要求 `WaitingJourneyRescueBatteryPercent` 低于强制充电线（`ChargingPolicyStartupCheck.Judge`）。
并行实例定义与包内 `appsettings.json` 都没有设这一项，取代码默认值 15（`JourneyRuntimeOptions.cs:119`）。15 < 30，成立。

**适用车辆范围为什么是 `[]`，而不是写成 agv02、agv03 两个键**（调度 10-03 同意）：

- 策略导入会整份拒绝 `--fleet` 之外的车：`vehicleScope` 里的键不在 `--fleet` 里时报 `CHARGING_POLICY_VEHICLE_OUTSIDE_FLEET`（`ChargingPolicyGovernance.cs:279`）。
- `--fleet` 必须照部署配置抄（batch-9 说明第四节），而并行实例是单车：`scripts/parallel/instance-factory01-v2.json` 里只有 agv02 的
  `vehicleKey`，`Test-ParallelInstance.ps1` 会拒绝 `Fleet`。
- 所以写成两个键的话，10-07 在并行实例上导入会被拒。`[]` 也是 09-29 批准、出口报告第五节第 5 条记的值；它覆盖配置里实际在跑的那台车。
  以后 agv03 进了部署配置，`[]` 自动覆盖它，不用另出版本。

## 三、演练版策略：`docs/field/charging-policy-map26-drill-20261008.json`（本 PR 新增）

与正式版只差两个值：`mandatoryChargeEntryThresholdPercent` 50、`chargingCompletionThresholdPercent` 62；其余六项逐字相同。依据是方案第七节第 6 条
「照 09-12 现场窗口的临时抬线」【读到】，批准与正式版同一次（第一节）。

- 硬关系代入：62 > 50 成立，50 ≥ 20 成立。
- 救命线：15 < 50，成立。
- **演练结束后必须换回正式版本。**做法照 batch-9 说明第四节「回退」：内容与最新一版相同时，导入会报 `UNCHANGED`，这时直接对正式版那个已批准的
  版本号再跑一次 `activate-charging-policy`。文件的 `changeNote` 也写了这一句。
- 用途：激活后，电量低于 50% 的车下一轮就会被派去充电，演练里就能当场触发一次充电（方案第六节第 4 条）。这一步属于会让车动的操作，要逐次授权。
- `docs/field/batch-9-charger-roster-and-charging-policy.md` 第四节末尾写着「本票不带那份文件」，那是 cs#400 当时的状态。按本票「只新增」的边界，
  那份说明本 PR 不改，以本节为准。

## 四、两份名册与站点目录

两份名册都是 cs#400 入库的文件，本 PR 不改：

| 文件 | 内容 | `approvedBy` |
| --- | --- | --- |
| `docs/field/charger-roster-map26-station211.json`（启用） | 26 号图站 211「充电点1」，进出点 212，`vehicleScope` 为空 | Zhengyu Shao |
| `docs/field/charger-roster-empty.json`（置空） | `chargers: []` | Zhengyu Shao |

`catalog-26.json` 是名册导入要的 `--catalog`，格式为 `{mapId, stations[{stationId, stationName}]}`。它**不是新读 RIoT 得来的**，而是由
`8005-workspace-v2/evidence/field/2026-10-01-map26-shared-nodes/raw-stations.json` 转换出来的。那份原始数据是 10-01 经用户授权、只读
`GET /api/imap/v1/mapInfo/stations/26` 的应答，SHA-256 为 `baf1eb3c…a7bb`。转换只取 `id`、`name` 两项，按 `id` 排序，共 211 站；
其中 211「充电点1」、212「充电准备点1」、214～216「等待点1～3」。转换没有改动任何一个站名。

## 五、静态校验（`--dry-run`）

**怎么跑的**：

- **库**：本机临时目录里的一个新 SQLite 文件，不是任何真实实例的库。由一个一次性小程序（只引用 `ControlServer.Infrastructure`，对该文件调
  `Database.MigrateAsync`，与测试夹具 `WaitingPointImportHarness` 同法）建表，迁移到 `20260929114754_Batch9ChargingPersistence`。
  没有用 `ControlServer.Host.exe --migrate-only`，没有启动服务，没有连 RIoT。
- **工具**：`fp/v2-impl@46148e35` 的 `tools/ControlServer.FieldOps`，Release 构建，SDK 8.0.425。
- **`--fleet`**：`BROKERX-f38975561adf46ccb1d2f23833c7d0e4`（agv02，照 `scripts/parallel/instance-factory01-v2.json` 抄）。

| 输入文件 | SHA-256 |
| --- | --- |
| `docs/field/charging-policy-map26.json` | `9c1d09c0b05f553a9cd04f0513efa8cb8b8bbff73d7bc90202518993829388ab` |
| `docs/field/charging-policy-map26-drill-20261008.json` | `23a29753d4501c584d63ae46b5183886d316ebe510d5bce9bedd8b67e452a465` |
| `docs/field/charger-roster-map26-station211.json` | `ac69cfbea5b9422835ec2f168c91de02b4422e9f0832236e7b732bba1477233c` |
| `docs/field/charger-roster-empty.json` | `4524d05180ee6ca3d4d3ead919adafc4e6bebf89d0d3bae0d3ebdf23bf10031f` |
| `catalog-26.json` | `55a1f20b846f672687bb08ac8a731e171824e6f0ff5c0da3ef379a7e1f983c16`（检出后的 LF 版本；跑预演时用的是 CRLF 工作副本，内容相同，提交后对 LF 版本复跑启用版名册预演仍是 `OK`、0 错） |

**结果**（全文在 `dry-run/`；退出码全部为 0）：

```
policy-formal      import-charging-policy --dry-run   outcome=OK errorCount=0
  content: margin 20, entry 30, completion 80, consumption 10, 180/600/3, vehicleScope []
  impact.coveredAfter=[BROKERX-f389…] vehiclesLosingPolicy=[] vehiclesWithoutPolicyAfter=[] openCycles=[]
policy-drill       import-charging-policy --dry-run   outcome=OK errorCount=0
  content: margin 20, entry 50, completion 62, consumption 10, 180/600/3, vehicleScope []
  impact.coveredAfter=[BROKERX-f389…] vehiclesLosingPolicy=[] vehiclesWithoutPolicyAfter=[] openCycles=[]
roster-station211  import-charger-roster --map 26 --dry-run   outcome=OK errorCount=0 entryCount=1
  changes: 211 ADDED {stationName 充电点1, entry 212, exit 212, vehicleScope []}; inProgress.openCycleCount=0
roster-empty       import-charger-roster --map 26 --dry-run   outcome=OK errorCount=0 entryCount=0 emptyRoster=true
  changes: []; windowCanClose=true "No charging is under way; the window can close."
```

预演确实没有写库：临时库在建表之后、以及全部 dry-run、反例与只读命令跑完之后，SHA-256 都是 `4b750884…7b23`。对它跑只读命令的结果是：`charging-policy` 报
`activeVersion=null`，agv02 `commissioned=false`、`CHARGING_POLICY_NOT_APPROVED`；`charger-roster` 报 `version=null`、`emptyRoster=true`。
这也说明了 10-07 导入之前实例会是什么样子：车不接活，名册为空。

**反例（证明校验真的会拒）**：

| 改动 | 结果 |
| --- | --- |
| 演练版把充满线改成 50（与强制充电线相等） | `REJECTED`，退出码 1，`CHARGING_POLICY_THRESHOLD_RELATION_VIOLATED`：「ChargingCompletionThreshold 50 must be greater than MandatoryChargeEntryThreshold 50 (REQ-0281).」 |
| 启用版把站名改成「充电点X」 | `REJECTED`，退出码 1，`CHARGER_ROSTER_STATION_NAME_MISMATCH`：「The file names station 211 '充电点X', the catalog '充电点1'.」 |

**限定，读结果时要知道**：

- 临时库里**没有等待点、任务类型固定站和机台站**，所以名册的三类冲突检查（站是等待点、站是任务类型固定站、站是机台站）在这里不可能报错。
  它们只有在真实例的库上导入时才会被检查。
- 临时库里也没有生效的策略，所以 `impact.changes` 全是「从无到有」，`vehiclesLosingPolicy` 恒为空。真实例上第二次及以后的导入，要按 batch-9 说明第四节读 diff。
- 输出里的 `input`、`catalog` 是本机路径，没有意义；对照用上表的哈希。

## 六、清桩出口的配置草稿

见 `config-drafts/README.md`。要点：

- 部署链今天带不上这两节，所以由用户在并行实例装好后手工合入；**每次首装或 `-Rollback` 之后都要重核**。
  冲掉时服务不报错，表现为告警 2271／2272、清桩没有出口。让部署链自己带上这两节由调度另开票。
- 10-07 用第一阶段那份（两个开关都关），或者干脆不合入 `VehicleFaultRecovery`。第二阶段那份要等清单第 13、14、16 条的核对完成后才用。
- 凭据只写变量名 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，值不进仓库。
- R-11／R-13 名单模板的 `operatorId` 留空：忘了填时服务端判「没人有权限」，是安全的；写成占位串反而会让服务端以为有人有权限
  （实测，`config-drafts/roster-template-check.txt`）。真实的操作员号要 10-07 由用户提供。

## 七、开关窗规程

用户 2026-09-29 定的 211 隔离方式（票面「来源」）：v2 只在用户授权的窗口里自动充电，窗口外名册置空；不改 MVP 配置。

- **默认状态**：名册是「置空」版。这时 v2 的车低电时退化成人工充电等待并告警（cs#404 的行为）；这是用户定的并行期运营方式，不是缺陷。
- **开窗**：先由用户在对话里授权本次窗口，并由现场确认 agv01 此刻不在 211 上充电、窗口内也不会去充。原因是 25、26 两张图上的 211／212
  坐标完全相同，是同一个物理桩，而 RIoT 按图管占用，看不见另一张图上的车。不改 MVP 配置，不读 MVP 生产库。确认之后，先 `--dry-run`
  导入「启用」版（`changes` 应是 `211 ADDED`），再正式导入。
- **关窗**：导入「置空」版，读输出里的 `inProgress.openCycles` 与 `windowCanClose`。`windowCanClose` 为 `false` 时，名册虽已置空、只挡新的分配，
  但列出的车还在 211 上或正开过去。这时不强行处置，等它充完离桩（再跑只读的 `charger-roster`，看 `inProgress` 变空），再让 agv01 用这个桩。
- **每一次**都由用户授权；每次开关窗的时间、授权人、agv01 状态确认、导入输出摘录，按第八节追加进本文件，只增不改。

命令格式见 `docs/field/batch-9-charger-roster-and-charging-policy.md` 第二节，10-07 的顺序见 `checklist-1007.md`。

## 八、导入与开关窗记录（10-07 起追加，只增不改）

| 时间 | 动作 | 机器／实例 | 授权人 | agv01 状态确认 | 输出摘录（文件） |
| --- | --- | --- | --- | --- | --- |
| （尚无） | | | | | |
| 2026-10-07 19:10–19:14 CST | 首装 v2 并行实例（`JourneyRuntime.enabled=false`），**半装**，见 8.1 | factory01，`8005 AGV ControlServer V2` | Zhengyu Shao（cs#411 issuecomment-6032690519、-6032938272） | 不适用（不开窗） | `import-1007/02-deploy-whatif.txt`、`03-deploy.txt`、`04-diagnose-fake-mes-ingest.txt`、`05-installed-config.txt` |
| 2026-10-08 08:56 CST | 导入正式策略 → v1；导入演练策略 → v2（只导入）；导入「置空」名册 → v1 | 同上，库 `C:\ProgramData\8005\ControlServer.V2\data\controlserver.db` | 同上 | 不适用（置空，不开窗） | `import-1007/07-fieldops-1.txt`、`08-fieldops-2.txt` |
| 2026-10-08 09:26 CST | 批准正式策略 v1（`--source FIELD`，`--role 产品负责人`） | 同上 | 同上；`--role` 由用户 10-07 在调度会话中确认（转述） | 不适用 | `import-1007/09-fieldops-3.txt` |
| 2026-10-08 09:28 CST | 激活正式策略 v1；核对 | 同上 | 同上 | 不适用 | `import-1007/10-fieldops-4.txt` |

### 8.1 10-07／10-08：装实例、导入、批准、激活（cs#411 第 4 步）

**授权**：用户 10-07（cs#411 issuecomment-6032690519）：在 factory01 的 v2 并行实例上装包，`JourneyRuntime.enabled=false`，导入并核对，不碰车。
同日补充授权（issuecomment-6032938272）：只列两个根目录的子目录名；服务启动后连 RIoT 网关（只读、不下单）在授权内；`--activated-by` 如实写 AI 代理。
执行者是工作会话（Claude，AI 代理），每一步都经调度会话（Coordinator 9）放行。

**装包**：

- 发布包：`release.yml` run 37585620196，`fp/v2-impl@d2018450`，车载端 `w2g/fp-v2-impl@022282eb`，包 SHA-256 `9186e4f1…b526`。
- 实例定义：取自 `feat/cs411-1007-field-import@7bd60584`，那里填了三个 map 26 值（见下）。部署用的覆盖定义 `import-1007/deploy-override-definition.json`
  只把 `routeGraph.enabled`、`journeyRuntime.enabled` 改成 `false`，其余逐键相同。
- 装前只列了 `C:\Program Files\8005 AGV` 与 `C:\ProgramData\8005` 的子目录名（`01-list-roots.txt`）：只有 MVP 的 `ControlServer`、`ControlServer-backups`，没有别的带 v2 字样的目录。
- **结果：半装。**产品安装脚本 PASS，配置覆盖层合入并回读核对，服务起来了（`172.19.205.222:58105`／`:58107`）。随后 FakeMesIngest 的计划任务在 120 秒内没有应答健康检查：

  ```
  FakeMesIngest did not answer http://127.0.0.1:58188/control/v1/health within 120 s.
  ```

  （FakeMesIngest 在 120 秒内没有应答健康检查。）任务 `LastTaskResult=4294967295`，日志目录没有建出来，说明包装脚本正文一行都没执行；原因另开 control-server#512 查，不在 factory01 上复现。
  失败点之后没有执行的步骤：防火墙规则、把包移入 `D:\zhengyushao\ControlServer.V2`。所以升级和回滚走不通，补装要先 `-Uninstall`（默认保留数据根，导入结果随库保留）。
- MVP 服务前后指纹一致（PID 6660，Running，58005／58007）；服务器上没有留下 `deploy-config.json`。
- 调度批准「先导入、后补装」，前提已实读核对：卸载默认保留数据根；重装遇到已有数据根时，先备份再继续使用，不清库。

**已装配置回读**（`05-installed-config.txt`）：`JourneyRuntime.enabled=false`、`RouteGraph.enabled=false`、`RiotCreateDispatch.enabled=false`、
`RiotForeignOrderCancel.enabled=false`、`VehicleFaultRecovery.enabled=false`；`ConnectionStrings:ControlServer = Data Source=C:\ProgramData\8005\ControlServer.V2\data\controlserver.db`；
`MesIngest.baseUrl=http://127.0.0.1:58188`；`minimumBatteryPercent` 在已装配置与包内 `appsettings.json` 里都是 0 处。

**FieldOps**：`fp/v2-impl@d2018450` 的 `tools/ControlServer.FieldOps`，self-contained win-x64 发布，放在 `D:\zhengyushao\control-server-v2-ops\fieldops-d2018450\`。
五个输入文件拷上去前的 SHA-256 与第五节的表逐个一致。`--database` 和 `--fleet` 由服务器端脚本从已装配置现读（`06-fieldops-head.ps1`），不手敲。每一步都先 `--dry-run`。

| 步骤 | 输出摘录 |
| --- | --- |
| 基线 `charging-policy` | `activeVersion=null`；agv02 `commissioned=false`，`CHARGING_POLICY_NOT_APPROVED` |
| 正式策略导入 | `OK`，`version 1`，`contentSha256 5d34c605…117a`，`vehiclesWithoutPolicyAfter=[]` |
| 置空名册导入（`--map 26`，`catalog-26.json`） | `OK`，`version 1`，`emptyRoster=true`，`windowCanClose=true`，`contentSha256 8939cc82…98f1` |
| 演练策略导入（只导入） | `OK`，`version 2`，`contentSha256 ad2a1a49…5541`；未批准、未激活 |
| 批准 v1 | `OK`，`approvedBy Zhengyu Shao`，`approverRole 产品负责人`，`source FIELD` |
| 激活 v1 | `OK`，`sequence 1`，`activatedBy Claude（AI 代理，Zhengyu Shao 10-07 授权，cs#411）`，`impact.vehiclesWithoutPolicyAfter=[]` |
| 核对 `charging-policy --fleet` | `activeVersion=1`；agv02 `commissioned=true`，`CHARGING_POLICY_EFFECTIVE`；v2 `approvals=[]` |
| 核对 `charger-roster` | `version 1`，`emptyRoster=true`，`inProgress.openCycleCount=0` |

批准的 `basisReference` 原文：「用户 2026-09-29 在调度会话中批准（转述），批次 9 方案第七节第 6 条；--role 由用户 10-07 在调度会话中确认（转述）；10-07 导入授权 cs#411 issuecomment-6032690519」。
`--role` 那一句在中段，不在末尾：调度后来要求「放在末尾」的消息到达时，批准已经写进库了。批准记录只能追加，不能修改，所以没有重做。

**没有做的**：没有查 RIoT（见 8.2）；没有开窗；没有激活演练策略；没有打开任何开关；没有启动车载端；没有碰 agv01、MVP 的配置与库、生产 MesIngest。

### 8.2 与清单和文档不符之处

- **清单第 2 条（删 `minimumBatteryPercent`）是空操作**：这个键在实例定义、包内 `appsettings.json`、部署脚本里都不存在，已装配置里也是 0 处。
- **清单第 3 条有一处与代码不符**：包内 `src/ControlServer.Host/appsettings.json:64` 设了 `waitingJourneyRescueBatteryPercent: 15`，不是「没有设」。值与代码默认值相同，15 < 30、15 < 50 的结论不变。
- **清单第 6 条已被 control-server#454 取代**：部署链按实例定义写 `VehicleFaultRecovery`（`enabled=false`）和 `FieldOperatorRoles`（名单路径在运维目录、空名单），不再手工合入。装完打出 `CLEARANCE_EXIT_UNAVAILABLE`，这是第一阶段的预期。
- **FieldOps 不在发布包里**，清单和 batch-9 说明都没写它在服务器上从哪来。这次是从同一个 commit 另外发布了一份。
- **map 26 的三个值不在 RIoT 里**：`dispatchZone`、`allowedDispatchZones` 是服务端自己的调度区名，`WIRE` 取自用户 2026-09-18 的决定
  （`evidence/field/2026-09-18-B4-site-prerequisites/03-area-assignment-table.md` 第 58、122 行）；`admissionPolicyDeploymentId` 是服务端写库的标签，由调度 10-07 定为
  `MAP-26-WIRE_TO_GATE-20261007`。`scripts/parallel/README.md` 和工作区 `wire-to-gate-parallel-cd.md` 第 9 节都写过「从 factory01 直查 RIoT 取回」，那是错的，已一并改正。
  运行时关着时这些值与站点清单都不被读取；**开运行时之前要先出 26 版 `task-type-stations.settings.json`**。
