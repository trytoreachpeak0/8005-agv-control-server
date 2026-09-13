# 缺陷：取货单确认后、到站前不发行程计划，与 CV-DEMAND-ACCEPT-TO-PICKUP 的顺序不符

Status: fixed
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs`、`src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs`）
Found by: 2026-09-13 为 `FP-IS-01` 补 G3 断言时对照协议向量 `vectors/CV-DEMAND-ACCEPT-TO-PICKUP/input.ndjson` 的代码走查（无门禁红证据：G3 此前没有这个切片的面）；随后由单元测试 `ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup` 在修复前的代码上复现为红
Product at discovery: `fp/b2-close@5db09c57`（即 `fp/v2-impl@65bffc0c` 加急停修复，与本缺陷无关）
Fixed in: `fp/b2-close@5293c43f`
Peers: 车载端 `w2g/b3-on-v2@c86bac5`（无需改动）

**红在产品，偏离的是协议契约。车载端按契约实现，两端都各自通过了自己的 G2。**

## 现象

`protocol-v1.0.0` 里 `CV-DEMAND-ACCEPT-TO-PICKUP` 的 `input.ndjson` 给出的顺序：

| 步 | 事件 |
| --- | --- |
| 1～2 | MesIngest 终读到一条需求；RIoT 建出 `TO_PICKUP` 单 |
| 3～4 | `UpcomingStopPlanSnapshot` → `SnapshotAppliedAck` |
| 5 | RIoT 报到达取货点 |
| 6～7 | `CurrentStopWorklistSnapshot` → `SnapshotAppliedAck` |
| 8～9 | `UpcomingStopPlanSnapshot`（下一个 revision）→ `SnapshotAppliedAck` |

服务端修复前的行为：**到站前一条快照都不发**；可信到站后一次发出业务状态、工作清单、计划、录入请求，计划只有一份。

## 为什么两道 G2 都没抓到

- **本仓 `CONTROL_SERVER_G2`**：`ProductionRuntimeResumesOneJourneyThroughTrustedArrivalsAndAtomicCompletion` 在到站后把发件箱的消息类型**排序**再比对，比的是「有这四种」，既不看顺序，也不看到站前有没有计划。
- **车载端 `ONBOARD_HMI_G2`**：`DemandAcceptanceSnapshotsArePersistedBeforeAcknowledgement` 的假服务端正好按向量顺序发三条快照，断言确认序列是「计划、清单、计划」、revision 为 1、1、2——对的是向量，不是本仓。
- 协议仓 `docs/candidate-limitations.md` 写明「没有任何断言执行器读过 `input.ndjson` 或 `expected.json`」。两端各自对着自己想象中的对端通过，这是那条限制的一个实例。

真车载端对缺这条快照是宽容的：L2 `real-onboard-normal-load` 修复前后都绿。G3 若照向量断言消息序列，会在这里红。

## 修复

- 取货单确认后（`EnsureMovementConfirmedAsync` 返回之后、查到站之前），**发一次**到站前计划：revision 为存储的 `PlanRevision`，取货段 `ACTIVE`、送货段 `PLANNED`。已经入了发件箱就不再调发布器，未确认的由每轮开头的重放负责，避免每轮评估往线上重复发同一行。消息号由 `DemandId` 确定性派生，无 migration；它也加入了重放白名单。
- 到站后的顺序本来就是清单在前、计划在后，保持；到站计划 revision 改为 `PlanRevision + 1`，关卡计划 `PlanRevision + 2`。
- 计划流每趟旅程占三个 revision（`PlanRevisionsPerJourney = 3`），同一辆车下一趟旅程从上一趟起点加三开始，永不回退。
- **到站时若到站前那份仍未确认，先作废（`FencedAt`）再发到站计划。**重放按旧到新发送未确认行，而车载端把低于已采纳 revision 的快照判 `SNAPSHOT_REVISION_REGRESSION` 并断开会话。到站前计划是唯一一条「期间不需要车载端发任何业务消息、旅程就会前进」的快照，确认丢失时它会排在更高 revision 后面被重放。快照只陈述当前状态，作废它不丢信息，车载端直接采纳更高的 revision。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增单元测试 `ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup`、`ThePlanSentBeforeArrivalIsRetiredWhenThePickupPlanSupersedesIt` | 修复前 2 条红，修复后绿 |
| 受影响的既有测试 | 五处期望随计划 revision 数量与发件箱内容调整，理由写在各自注释里；其中「重新封装」那条改为只要求未作废的行升代，作废行保持原代 |
| 全量 | `709 passed / 0 skipped`（`5293c43f`） |
| [`evidence/l2/20260913-b2close-normal-load-plan-before-arrival-001`](../../evidence/l2/20260913-b2close-normal-load-plan-before-arrival-001/SUMMARY.md) | 合成车载端，`5293c43f`，9 条 PASS，日志无 `ProtocolProblem` / `SNAPSHOT_REVISION` |
| [`evidence/l2/20260913-b2close-real-onboard-normal-load-003`](../../evidence/l2/20260913-b2close-real-onboard-normal-load-003/SUMMARY.md) | 真车载端 `c86bac5`，`5293c43f`，12 条 PASS，日志同上无命中：真车载端先采纳 revision 1 的到站前计划、再采纳到站计划 |

消息序列本身在 G3 `FP-IS-01` 的断言里核对，那一份证据出来后补进本表。
