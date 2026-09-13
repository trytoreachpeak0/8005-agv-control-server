# 缺陷：出发前安全检查一旦失效，服务端不再重新发起，旅程永久停在取货点

Status: fixed（服务端）；车载端一半在车载端仓 `docs/W2G_PREDEPARTURE_CHECK_EXPIRED.md`
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs`）
Found by: 2026-09-13 为 `FP-IS-03` 设计 G3 场景时，对照协议向量 `CV-PREDEPARTURE-SAFETY-EXPIRES` 的代码走查（无门禁红证据：G3 此前没有这个切片的面）；随后由两条单元测试在修复前的代码上复现为红
Product at discovery: `fp/b2-close@dce27490`
Fixed in: 见提交记录（本单与修复同一提交）
Peers: 车载端 `w2g/b3-on-v2`（同日另有车载端改动，见上）

**红在产品。两端各自的 G2 都绿；这条向量的四条消息里有一条两端都从没发过。**

## 向量与决定

`CV-PREDEPARTURE-SAFETY-EXPIRES` 的顺序：`PreDepartureSafetyCheck` → `PreDepartureSafetyCheckResult` → `SafetyStateChanged` → `ProtocolProblem`，
`stableErrorCode = PREDEPARTURE_CHECK_EXPIRED`；服务端 `EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE`、`NEVER_DEPART_ON_EXPIRED_CHECK`，
车载端 `REPORT_SAFETY_STATE_CHANGE_PROMPTLY`、`REREQUEST_CHECK_AFTER_EXPIRY`。

协议没有说 `PREDEPARTURE_CHECK_EXPIRED` 由哪一端在什么时候发。2026-09-13 用户裁定按向量两端都补齐，解读如下：

- 服务端：检查失效后作废旧检查，以新身份、按当前安全版本重新发起。
- 车载端：收到询问的安全版本已经落后于本端被接受的版本的检查，不作答，回 `ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED)`，会话不断。

由服务端发这条 `ProtocolProblem` 走不通：车载端发结果时等的是 `DurableAck`，服务端收到结果当即确认；事后再来一条关联到它的
`ProtocolProblem`，车载端找不到等待者，按未处理消息断开会话。

## 现象（服务端）

- 一趟旅程只有一个 `PreDepartureSafetyCheckId`（`WireToGateStore.ToRuntimeRow` 由需求确定性派生）。
- 答复不成立（安全版本与会话不一致、窗口已过、UNSAFE……）时 `FindSafeDepartureResultAsync` 置 `PRE_DEPARTURE_SAFETY_NOT_VALID` 返回。
- 之后每一轮都读到同一份失效的答复，同一个检查不会被再问。旅程停在 `AwaitingDepartureSafety`，不会自己恢复。

`NEVER_DEPART_ON_EXPIRED_CHECK` 一直成立；缺的是检查失效之后怎么往下走。

## 修复

`AwaitingDepartureSafety` 找不到可用答复时，`ReissueExpiredDepartureCheckAsync` 判当前检查是否已过期：

- 检查询问的 `expectedSafetyStateVersion` 已不是会话当前的安全版本（不论答没答）；或
- 它的答复窗口已过、或答复的安全版本与会话不一致。

且会话此刻 `DepartureSafe = true`（车不安全时新检查只能被答 UNSAFE，且会逐轮重问）。成立时：

- 旧检查的发件箱行置 `FencedAt`；
- 检查号与消息号由旧值确定性派生出新值（`StableGuid(旧值, "reissued-after-expiry")`），重启后派生出同一对，不加列、无迁移；
- 按当前安全版本发新的 `PreDepartureSafetyCheck`，`BlockReasonCode = PREDEPARTURE_CHECK_EXPIRED`；
- 同一轮立即去判新检查的答复（与首次检查同样的理由：车载端给的窗口比轮询间隔短），这一轮最多重发一次。

每一轮开头的重放仍会先把尚未作废的旧检查补发一次；车载端版本已前进时，正是它引出向量里那条 `ProtocolProblem`。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 `ASafetyChangeAfterTheAnswerExpiresTheCheckAndTheServerAsksAgainUnderANewIdentity` | 修复前红（`PRE_DEPARTURE_SAFETY_NOT_VALID`，检查号不变），修复后绿：旧行作废、新检查询问版本 8、凭新答复出发、`TO_GATE` 恰好一张 |
| 新增 `AnUnansweredCheckIsAskedAgainOnceSafetyHasMovedOnButNotWhileTheVehicleIsUnsafe` | 修复前红（安全恢复后检查号不变），修复后绿；车不安全时不重问 |
| 既有 `UnknownPreDepartureSafetyCannotCreateTheGateOrder`、`DepartureSafetyAnsweredPromptlyIsJudgedWhileItIsStillValid` | 不变，绿 |
| `JourneyRuntimeWorkerTests` | 71 passed |
| 全量 | 718 passed |

真车载端上的完整顺序在 G3 `FP-IS-03` 里核对，那份证据出来后补进本表。
