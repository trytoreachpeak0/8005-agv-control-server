# `G3`：`FP-IS-15` **`PASS`**（纠正后的那一份）

`STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT`，`assuranceLevel` 为 `STAGED_REBUILD`，
`formalSlicePass: true`。真实三进程：`ControlServer.Host`、`SQCD_8005AGV_Simulator`、
`SQCD.Agv.Wpf`，全部从各自 commit 的一次性克隆重建。

## 它纠正的是哪一份

**`../20260910-fp-is-15-v2-alarm-snapshot/` 是 `FAIL`，原样保留、未改一字。**

那一次红在**断言**，不在产品。原断言 `onboardAlarmSnapshotPublishedOnEveryConnection` 假定每次
连接都发一份告警快照；实际是每次**完整**握手发一份。恢复重连走
`WireToGateSessionClient.HandshakeAsync` 的 `resumingInterruptedRecovery` 分支，只重放未确认的
那一条，三份快照一份都不重发——它们在上一个连接里已经被服务端接受过了。而这个 runner 的 replay
场景正是掐掉 `RecoveryStateReport` 的 `DurableAck` 并关连接，所以重连**必然**走恢复分支。

纠正的方式不是放宽那条断言，而是**把那个行为反过来钉住**：新增
`onboardAlarmSnapshotNotRepublishedOnRecoveryResume`。恢复重连若真的重发，投影会收到一份内容
相同、会话代却是新的快照，采纳规则会把它当成新消息——那才是缺陷，现在它有断言了。

## 结论

| 断言 | 结果 |
| --- | --- |
| `onboardAlarmSnapshotPublishedOnTheFullHandshake` | `PASS` |
| `onboardAlarmSnapshotAppliedAckOnEverySnapshot` | `PASS` |
| `onboardAlarmSnapshotNotRepublishedOnRecoveryResume` | `PASS` |
| `onboardAlarmProjectionIsASingletonPerVehicle` | `PASS` |
| `onboardAlarmProjectionCarriesTheGenerationItArrivedIn` | `PASS` |
| `identityRejections`（run-wide） | `PASS` |
| `noMovementOrExternalSideEffects`（run-wide） | `PASS` |
| `secretScan`（run-wide） | `PASS` |

绑定：

| | commit |
| --- | --- |
| `controlServer` ＝ `harness` | `cda644a128efc7ceea1089b834b26792cc241293` |
| `onboardEvidenceBinding` | `afba86e07116192bde386937c559cc7a70a00a7f` |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |

`harnessWorktreeCleanAtStart: true`。

## 装置里两处与既往不同，都要读到

**一、车载端不是从 GitHub 拉的。**`-OnboardRepository` 指向本地
`C:\Users\szy\8005-b3\hmi-b3`，`-OnboardRemoteRef origin/w2g/b3-on-v2`。批次 3 在 v2 线上的
车载端那一半在 `w2g/b3-on-v2`，**这条分支尚未推送到远端**（推送要用户批准，本轮没有）。

`RemoteRef` 断言本身没有被取消：克隆源仍然必须把 `afba86e0` 认作某个分支的顶，所以证据绑不上
一个只以游离对象形式递给 runner 的 commit。**但「远端」在这一次指的是本地那个仓**，
不是 `github.com/trytoreachpeak0/8005-agv-onboard-hmi`。判定这份证据的可复现性时要知道这一点。

**二、协议仓也是本地的**（`C:\Users\szy\8005-b3\proto-g1`，`f6ee75d` 的普通克隆）。
runner 内部会自己跑一次协议 `G1`，日志在 `logs/protocol-g1.log`。

## 未在本轮证明的

- **这一次的告警集是空的**（`projectionAlarmCount: 0`，`snapshotsSent: 1`）。所以证到的是
  「一份快照被 ack、被投影、带着它到达时的会话代、一车一行」，**没有**证到带非空告警内容的
  投影，也**没有**证到「后一份整体取代前一份」——这个装置里只到达了一份。
  那两件事有单元测试与 `CONTROL_SERVER_G2` 覆盖，但那不是 `G3`。
- **「车重启后序号回到 1 仍被采纳」没有证。**它是 `(会话代, 序号)` 采纳规则的另一半，要车载端
  进程真的重启才能到达，归 `run-staged-g3-restart.ps1` 的场景。本 runner 的 claim 注释里写明了
  这个归属，没有在这里冒领。
- **`FP-IS-14` 没有跑，而且今天跑不了。**不是缺一条断言，是它要认证的那件事在运行中的系统里
  没有发生的途径：`SlotConfigurationActivationDispatcher.IssueAsync` 没有 HTTP 端点，
  `ControlServer.FieldOps` 也没有对应动词，唯一的调用者是测试；而 `ActiveSlotConfigurationRow`
  只在一次激活收敛时才写，所以受控装置里的服务端手上永远没有已批准版本，
  `ReconcileReportedFingerprintAsync` 只会走「没有可比对的对象」那条分支，直接返回
  `Agrees: true` 而**没有比过**。硬跑会产出一份「握手通过了，但那个通过没核验任何东西」的记录，
  比没有证据更坏。已登记在 `scripts/g3-slice-evidence.ps1` 的
  `Get-G3SlicesWithoutSurfaceThisBatch` 里。
- **因此「两端指纹算法一致」仍然没有被端到端证明。**两个仓各钉一个相同的固定值，只能证各自
  没跑偏。要证它，需要先补上激活的发起入口。
- `protocol-v1.0.0` 这个 tag **尚未打**，attestation 是 `PENDING` / `TRACKED_TEMPLATE`。
  本次绑的是 commit，不是 tag。

## 与协议 `G1` 的关系

同日的协议 `G1` 是 `PASS`，记录在 `../../g1/20260910-fp-is-14-15-protocol-g1/`。
本次 `protocolManifestSha256` 是 `84f984ea…`，与那份 `G1` 的 `candidateManifestSha256`
以及两份 `CONTROL_SERVER_G2` 的同名字段逐字相同。
