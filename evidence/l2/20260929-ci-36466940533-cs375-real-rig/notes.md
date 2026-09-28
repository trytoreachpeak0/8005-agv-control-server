# control-server#375 审查返工后的真装置验收：CI 真装置 run 36466940533

审查返工（必修 M1：补建前过建单门禁；S3～S6）之后，在 `c8822cec` 上跑 `real-onboard-unread-pre-create-read` 1 遍。
这一版场景有两段：取货腿、关卡腿各注入一次建单前读失败。持有由 Coordinator 8 于 2026-09-29 放行，跑完已交还。
上一轮（run 36446819199，只有取货腿）的证据留在 `evidence/l2/20260928-ci-36446819199-cs375-real-rig/`，不改。

## 四行核对

三行提交取自场景那一步（`Run real-onboard L2 scenarios`），与派发清单逐位一致：

```
control-server @ c8822cec29351aab517a3900b15e120f8073b3f2
8005-agv-onboard-hmi @ 60f341878a1bce705598aa9043234c1ca2fb1d7a
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
Mode default; real-onboard-unread-pre-create-read
real-onboard-unread-pre-create-read-01 -- PASS, 71s
```

- 模式 `default`：依据是 `l2.yml` real-rig 作业里按 `RIG_MODE` 定遍数的那段（`default` 固定 1 遍，`consecutive-all` 至少 3 遍），
  这一轮只跑了 1 遍。
- 停止条件 `RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE|NOT_STARTED` 命中 9 行，全部带字面 `^[[36;1m`（源码回显）；
  去掉回显与环境变量块后命中 0。
- artifact `real-rig-evidence` 179632 字节，先核非空再做以上核对。
- 判据 9 条全部 PASS（`L2-UR-01-A/B/C`、`L2-UR-02`、`L2-UR-03`、`L2-UR-04-A/B/C`、`L2-UR-05`）。

## 补建前的两道检查：哪些是读到的，哪些是推的

**读到的**（两份摘录都摘自 `logs/control-server.out.log`，只留 HTTP 请求行与 SQL 的 `FROM` 行）：

- `control-server-pickup-retry-excerpt.log`（原日志第 3516～3870 行）：取货腿第一次按单号读在 02:43:13，假 RIoT 回 503；
  第二次读在 02:43:14。
- `control-server-gate-retry-excerpt.log`（原日志第 19457～19995 行）：关卡腿第一次读在 02:43:53；第二次读在 02:43:54。
- 两段里，第二次按单号读之前依次是：
  1. 读这张意图与 `RiotDispatchAuditEvents`：本票的「从没发出过」判定（`WireToGateStore.IsNeverSentAsync`）。
  2. 读会话就绪行（`SessionRecoveries`）与车载端报文（`ProtocolInbox`）、读故障表（`VehicleFaultStates`）、
     `GET getVehicleInfoByDeviceKey`、`GET task/v1/task/getVehicleInfo` 与按状态 1/3/7/9 的订单列表：`VehicleConditionReasonsAsync`。
  3. 读目录状态（`MapStationCatalogStates`）、任务类型暂停（`TaskTypeStationHolds`）、冻结站点（`FrozenDemandStations`）、
     `POST getRouteCostsBy`、写 `CreateGateAudit`：`GateLegAsync` 的建单门禁。
  4. 读意图、实验授权与审计表（`GetByUpperIdAsync`），然后按单号读，审计链紧接着是 `PRE_CREATE/NOT_FOUND` → `CREATE_DISPATCH/ARMED`。

**推的**：「两道检查的结果都是放行」没有专门的日志行或落库的码。依据是代码结构：
`JourneyRuntimeEngine.EnsureMovementConfirmedAsync` 对从没发出过的意图，车况不放行就写 `{腿}_CREATE_WAITING_VEHICLE` 返回，
门禁不放行就写门禁的码返回，两种情况都不会去按单号读；这一轮在两道检查之后读了单并建了单，所以两次都是放行。
关卡腿那一次车上有货，车况检查读的是真车载端报上来的离站摘要。

## 本目录留了什么

场景的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，上面两份日志摘录，与顶层 `commits.json`；完整的 `logs/` 与
`snapshots/`（含 `RiotDispatchAuditEvents`）在 run 的 artifact 里。PASS 不代表真实 RCS、真车、真实 IO 模块或接线合格。
