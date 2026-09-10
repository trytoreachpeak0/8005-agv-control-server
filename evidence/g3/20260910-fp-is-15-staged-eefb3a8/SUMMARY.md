# `G3`（主 runner）：`FP-IS-15` **`PASS`**（`eefb3a8`）

`STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT`，`assuranceLevel` 为 `STAGED_REBUILD`，30 条断言全绿。
本轮带 `-Slice FP-IS-15`：场景照常整条跑完，**只为 `FP-IS-15` 写 `gate-result.json`**。

## 为什么只证这一片

`eefb3a8` 修的是 `FP-IS-15` 的看板失联判定（`OnboardAlarmProjectionStore`，缺陷单
`docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md`），没有碰 `FP-IS-14`、`FP-IS-00`、
`FP-IS-06` 的任何代码。用户 2026-09-10 批准的是重跑 `FP-IS-15` 的门禁。

所以：

- **`FP-IS-15`**：本目录取代 `../20260910-fp-is-14-15-staged-6dc4bc8/slices/FP-IS-15/` 作为主 runner 的现行证据。
- **`FP-IS-14`、`FP-IS-00`、`FP-IS-06`**：现行证据仍是 `../20260910-fp-is-14-15-staged-6dc4bc8/`。本轮
  `run-result.json` 的 `classification` 里这三片同样是 `PASS`，但没有写 `gate-result.json`，不当作它们的门禁证据。

`6dc4bc8` 那一份原样保留、未改一字。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `eefb3a8802664623fdde9dd7bdc759ea5b61a5b0`（param 块默认值，`723ee60` 移过来的） |
| `onboard` | `afba86e07116192bde386937c559cc7a70a00a7f`（`origin/w2g/b3-on-v2` 的顶） |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b`，`tagExists: false` |
| `harness` | `869b150`，`harnessWorktreeCleanAtStart: true` |

与同日 `CONTROL_SERVER_G2`（`../../g2/20260910-fp-is-15-alarm-liveness-eefb3a8/`）绑同一个服务端 commit。

## 实读

```
clientConnectionIds        [1, 2, 3]
fullHandshakeConnectionIds [1, 3]
snapshotConnectionIds      [1, 3]
snapshotSessionGenerations [1, 3]
appliedAcks                2
projectionRowsForThisVehicle 1   (sessionGeneration 3, snapshotSequence 2)
```

`FP-IS-15` 的六条全绿：完整握手发一份、一对一 ack、恢复重连不重发、多份只留最新、一车一行、带着到达时的会话代。
与 `6dc4bc8` 那一份的实读逐项相同——修复只改了看板读投影时的在线判定，快照怎么收、怎么存没有变。

## 未在本轮证明的

- **看板失联判定本身不在 G3 的断言里。**这个 runner 读的是库里的快照行，不渲染看板。「断线 → 看板显示失联 →
  重连恢复」由 L2 `onboard-alarm-snapshot-dashboard` 证。
- **车上告警内容仍然是空的**（`projectionAlarmCount: 0`）：真车载端 `OnboardAlarmBoard.Raise` 没有调用者。
- `fullG3` 与 `releaseCandidate` 仍是 `INCONCLUSIVE`（`FP-IS-01`／`02`／`03`／`07` 本批次无面）。
- `protocol-v1.0.0` 这个 tag **尚未打**。本次绑的是 commit。
