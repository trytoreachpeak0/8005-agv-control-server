# 10-07 投运清单：v2 并行实例的充电参数与清桩出口（cs#411）

按顺序做。每一步后面的【出处】是这一步的依据。评论编号见文末。本清单只是规程，写它的会话没有在任何机器上执行过其中任何一步。

## 一、导入之前

1. **确认机器、实例与时间**：导入只写并行实例的库，经调度与用户对过时间和机器之后才做。不碰 MVP 服务的进程、目录、配置和库。【票面第 4 步】
2. **从 v2 实例配置里删掉 `minimumBatteryPercent`**，否则服务拒绝启动。【评论 C3】
3. **核对救命线**：`JourneyRuntime:WaitingJourneyRescueBatteryPercent` 在并行实例定义与包内 `appsettings.json` 里都没有设，取代码默认值 15
   （`JourneyRuntimeOptions.cs:119`）。正式版 15 < 30，演练版 15 < 50，两版都满足。要是有人另外设了这一项，就按新值重核。【评论 C3；SUMMARY 第三节】
4. **确认 `--role` 的值**：批准命令的 `--role` 暂定「产品负责人」，**导入前请用户确认**。【调度 10-03 答复】

## 二、装包与配置

5. 跑 `scripts/parallel/Install-ParallelInstanceLocal.ps1`。它装完就重启服务，服务会自己迁移并行实例的库。**不要单独运行
   `ControlServer.Host.exe --migrate-only`**：它取不到连接串时，迁移的是 MVP 生产库。【评论 C2；`docs/field/batch-9-charger-roster-and-charging-policy.md` 第一节】
6. 在并行实例安装目录的 `appsettings.Production.json` 里合入 `config-drafts/appsettings.Production.phase1.draft.json`：两个开关都关，只配名单路径。
   也可以先完全不合入 `VehicleFaultRecovery` 一节，效果相同。名单文件按 `config-drafts/field-operator-roles.template.json` 填好后放到 `path`
   指向的位置。**每次首装或 `-Rollback` 之后都要重核这两节**：冲掉时服务不报错，表现为告警 2271／2272、清桩没有出口。【评论 C4、C5；`config-drafts/README.md`】
7. 导入策略之前，先拿到 `--fleet` 要用的 `VehicleKey`：从部署配置抄，不要手敲。今天的并行实例是单车，键只有一个，即 agv02 的
   `BROKERX-f38975561adf46ccb1d2f23833c7d0e4`。`--database` 也从并行实例安装目录的 `appsettings.Production.json`
   （`ConnectionStrings:ControlServer`）照抄。【batch-9 说明第一、四节】

## 三、导入什么（服务运行中做）

8. **正式策略**：`docs/field/charging-policy-map26.json`。先导入，再用 `--source FIELD` 批准，然后激活。**现场不带 `--allow-non-field-approval`。**【票面第 4 步；batch-9 说明第四节】
9. **核对**：激活输出的 `impact.vehiclesWithoutPolicyAfter` 必须是 `[]`；只读命令 `charging-policy --fleet` 输出里每辆车的
   `commissioned` 都必须是 `true`。两处都对上，才算投运。【评论 C2；batch-9 说明第四节】
10. **名册导入「置空」版** `docs/field/charger-roster-empty.json`（`--map 26`），作为默认状态。窗口外就保持这一版。`--catalog` 有两个来源：
    本目录的 `catalog-26.json`（10-01 读取，出处见 SUMMARY 第四节），或者现场用 `scripts/field/Export-RiotStationCatalog.ps1 -MapId 26` 新导出的一份。
    后者要读 RIoT，做之前先问用户。【票面第 4 步；出口报告第五节第 4 条；`docs/field/batch-8-waiting-point-registration.md`】
11. 用只读命令 `charger-roster` 与 `charging-policy` 再核一次当前生效的版本，把输出摘录追加进 `SUMMARY.md`。【票面第 4 步】
12. 演练策略 `docs/field/charging-policy-map26-drill-20261008.json` **10-07 不激活**。10-08 演练前经授权激活，演练结束后换回正式版本。
    这份版本 10-07 可以先导入、批准，留着备用。【方案第六节第 4、13 条】

## 四、哪些开关保持关闭，为什么

| 开关 | 10-07 状态 | 为什么关；什么时候能开 |
| --- | --- | --- |
| `VehicleFaultRecovery:enabled`（Host 恢复入口） | 关，或不合入这一节 | 第五节第 13、14 条两项现场核对都确认之前不开：RIoT 停充电量低于完成阈值时，每次正常充满都会被判成「中断」，桩和车会被隔离。开的时候用 phase2 草稿，设好凭据变量，并重启服务。【评论 C7、C8；出口报告第六节表】 |
| `FieldOperatorRoles:OnboardClearanceEntryDeclared` | `false` | 声明全车队共用一个值，服务端看不到每台车的情况。逐车核对过第五节第 16 条之后才设 `true`。【评论 C5、C6】 |
| `JourneyRuntime:ClearanceToWaitingPointEnabled` | 默认关，不设 | 三个前提都还没满足：白名单 1.2「形态一」补上「清桩开往等待点」；现场知情（RIoT 里结束旧单的那一刻车会自己开走）；车载端确认途中仍能取到原桩站号、按钮还在。【评论 C9、C10；出口报告第五节第 11 条、第六节表】 |
| `JourneyRuntime:UnableToChargeOldOrderCancelEnabled` | 默认关，不设 | 取消 `HANG` 充电单（`act(78,2,0)`）会不会让车动，现场没验证过；打开后第一轮就发取消，车还在桩上；急停或切手动造成的 `HANG` 一旦被确认也会被取消。【评论 C4、C6、C11；出口报告第六节表】 |
| 车载端 `wireToGate.unableToChargeEntryEnabled` | 车载端出厂关 | 属车载端配置，上车时在车载端现场配置里打开；服务端已支持（cs#410）。【出口报告第六节表】 |

开关都关着时：清桩出口不可用，「充不上」照旧按 `ORDER_HANG` 处理，启动时告警 2272、每个周期告警 2271；中断和无进展只告警。这是预期的行为，不是故障。【评论 C5；出口报告第五节第 8 条】

## 五、现场核对（开相应开关或开窗之前）

13. **在 RIoT 里查 agv02、agv03 所在车组的充电配置**：重点看 `batteryLevelFullyRecharged` 与 `keepRechargingUntilFullyCharged`。行为实验室第 24 轮读到的另一车组是 85／`false`。
    这一项是只读查询，但要碰 RIoT，做之前先问用户。【评论 C8】
14. **看一次实际停充时 RIoT 报的电量读数**：要真的充一次电，属于会让车动的操作，归入第六节的逐次授权。读数低于完成阈值（正式版 80、演练版 62）时，不开 Host 入口，结果报调度。【评论 C8】
15. **每次开窗之前**：现场确认 agv01 不在 211 上充电，在窗口内也不会去充。理由是 25、26 两张图上的 211／212 坐标相同，是同一个物理桩。
    由用户确认；不改 MVP 配置，不读 MVP 生产库。【票面第 5 步与「来源」；方案第六节第 3 条】
16. **逐车核对清桩入口**：这台车的维护开关（`wireToGate.recoveryResumeEnabled`）已打开；车上配置的操作员号在 R-11／R-13 名单里，而且名单条目写了 `operatorId`。【评论 C4、C6】

## 六、哪些步骤要用户逐次授权

都属工作区「Ask first」第 1 类（会让车动或发急停），每一次单独授权，现场有人盯着、物理急停可按。【方案第六节；工作区 `CLAUDE.md`「Ask first」】

- 启动接真 RIoT 的 v2 实例；在 agv02／agv03 上起车载端（`-SimulatorOnly` 除外）。
- **开窗**（导入「启用」名册 `docs/field/charger-roster-map26-station211.json`），以及**关窗**（导入「置空」版；`windowCanClose` 为 `false` 时先等进行中的周期结束）。【票面第 5 步】
- 激活演练策略（激活后下一轮派车就会让车开往 211），以及演练结束后换回正式版本。
- 第一次自动进桩、充满离桩、制造「充不上」或「充电中断」、人工清桩确认、桩恢复确认、充电后返回服务、任何急停。
- 第五节第 14 条那次实充看读数。
- 打开 `UnableToChargeOldOrderCancelEnabled` 之前，在空载的 agv02 上、有人盯着时实测一次取消。【评论 C6、C11】

## 评论编号（cs#411）

- C1 09-29 18:00 导入顺序（已被 C2 更正）：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5895760382
- C2 09-29 20:40 更正：不要单独跑 `--migrate-only`：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5898434484
- C3 09-30 03:33 救命线、删 `minimumBatteryPercent`：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5903520568
- C4 10-01 15:12 清桩出口三项配置：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5934344176
- C5 10-01 17:44 `OnboardClearanceEntryDeclared`：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5937083338
- C6 10-01 18:27 逐车核对、取消开关的实测时机：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5937870649
- C7 10-01 23:34 隔离的形成条件：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5942724990
- C8 10-02 03:32 RIoT 车组充电配置与停充读数：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5945111159
- C9 10-02 07:08 `ClearanceToWaitingPointEnabled`：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5947148148
- C10 10-02 08:32 同上，车载端前提：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5948262124
- C11 10-02 09:17 `UnableToChargeOldOrderCancelEnabled`：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/411#issuecomment-5949007512

时间为 UTC，取自 GitHub 的 `created_at`。正文开头写的日期是调度写评论时用的北京时间日期，所以与这里的 UTC 日期可能差一天。
