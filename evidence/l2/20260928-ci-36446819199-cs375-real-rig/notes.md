# control-server#375 真装置验收：CI 真装置 run 36446819199

新场景 `real-onboard-unread-pre-create-read` 第一次运行，1 遍。持有由 Coordinator 8 于 2026-09-28 放行，
跑完已交还；四行核对调度也独立核过。

## 四行核对

三行提交取自场景那一步（`Run real-onboard L2 scenarios`），与派发清单逐位一致：

```
control-server @ 1a73d80e76e89f8af30fb693310ebea17f842add
8005-agv-onboard-hmi @ 60f341878a1bce705598aa9043234c1ca2fb1d7a
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
real-onboard-unread-pre-create-read-01 -- PASS, 32s
```

停止条件 `RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE|NOT_STARTED` 命中 8 行，全部带字面 `^[[36;1m`
（源码回显）；去掉回显与环境变量块后命中 0。artifact `real-rig-evidence` 74683 字节，先核非空再做以上核对。
判据 L2-UR-01～05 全部 PASS。

## 这一轮跑的产品代码与 PR 最终 head 的关系

这一轮的 `control-server` 是 `1a73d80e`。之后本分支只加了证据文件（本目录与两个合成 L2 目录），`src/`、
`tools/`、`scripts/`、`.github/` 一行未动，所以这一轮对最终 head 仍然作数。若之后再有产品代码改动，以 PR 正文
里的对照为准。

## L2-UR-03「车况检查放行了这一次」：哪些是读到的，哪些是推的

**读到的**（`real-onboard-unread-pre-create-read-01/control-server-vehicle-check-excerpt.log`，摘自
`logs/control-server.out.log` 第 2698～2995 行，只留 HTTP 请求行与 SQL 的 `FROM` 行）：

- 第一次按单号读 `detailByUpperId/...-PICKUP-1` 在 23:54:11，假 RIoT 回 503，审计记为
  `PRE_CREATE_RECONCILIATION/UNKNOWN/SdkFailure`。
- 第二次按单号读在 23:54:12。紧挨在它之前，依次是：读这张意图（`OrderIntents`）→ 读会话就绪行
  （`SessionRecoveries`）与车载端报文（`ProtocolInbox`）→ 读故障表（`VehicleFaultStates`）→
  `GET getVehicleInfoByDeviceKey`（`ReadVehicleAsync`）→ `GET task/v1/task/getVehicleInfo` 与按状态 1/3/7/9
  的订单列表（车辆安全读取）→ 读意图、实验授权与 `RiotDispatchAuditEvents`（本票的资格判定
  `IsNeverSentAfterUnansweredReadsAsync`）→ 按单号读。前四段正是 `VehicleConditionReasonsAsync` 的读取顺序。
- 审计链紧接着是 `PRE_CREATE/NOT_FOUND` → `CREATE_DISPATCH/ARMED`，前后只 arm 一次。

**推的**：「检查结果是放行」没有专门的日志行或落库的码。依据是代码结构：
`JourneyRuntimeEngine.EnsureMovementConfirmedAsync` 对从没发出过的意图先调 `VehicleConditionReasonsAsync`，
不放行时写 `PICKUP_CREATE_WAITING_VEHICLE` 并直接返回，不会去读 RIoT 的单；这一轮读了单并建了单，
所以那一次检查的结果是放行。受理那一轮只调一次派车，所以第二次读只可能来自这条路径。

## 本目录留了什么

场景的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，上面那份日志摘录，与顶层 `commits.json`；`logs/` 与
`snapshots/`（含新加入快照清单的 `RiotDispatchAuditEvents`）在 run 的 artifact 里。
PASS 不代表真实 RCS、真车、真实 IO 模块或接线合格。
