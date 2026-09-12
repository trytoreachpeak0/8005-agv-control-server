# `G3`（进程重启）：`FP-IS-15` **`PASS`**（`eefb3a8`）

`STAGED_G3_PROCESS_RESTART_NO_MOVEMENT`，`status` 为 `STAGED_G3_PROCESS_RESTART_PASS`，25 条断言全绿。
本轮带 `-Slice FP-IS-15`：三个阶段照常整条跑完，**只为 `FP-IS-15` 写 `gate-result.json`**。

## 为什么只证这一片

同 `../20260910-fp-is-15-staged-eefb3a8/SUMMARY.md`：`eefb3a8` 只改了 `FP-IS-15` 的看板失联判定
（`OnboardAlarmProjectionStore`），用户 2026-09-10 批准的是重跑 `FP-IS-15` 的门禁。

- **`FP-IS-15`**：本目录取代 `../20260910-fp-is-14-fingerprint-mismatch-mapped/slices/FP-IS-15/` 作为进程重启 runner
  的现行证据。
- **`FP-IS-14`（拒绝路径）、`FP-IS-00`、`FP-IS-06`**：现行证据仍是 `../20260910-fp-is-14-fingerprint-mismatch-mapped/`。
  本轮那三条拒绝路径断言同样全绿，但没有写 `gate-result.json`，不当作它们的门禁证据。

被取代的那一份原样保留、未改一字。

## 绑定

commit 绑定照旧从 `scripts/run-staged-g3.ps1` 的 param 块读回（`commitBindingSharedWithMainRunner` `PASS`），
`723ee60` 把那里的服务端默认值移到了 `eefb3a8`。

| | commit |
| --- | --- |
| `controlServer` | `eefb3a8802664623fdde9dd7bdc759ea5b61a5b0` |
| `onboardHmi` | `afba86e07116192bde386937c559cc7a70a00a7f` |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |
| `runner` | `e8bbfe5`，`runnerWorktreeCleanAtStart: true` |

## 实读

`FP-IS-15` 的两条：

| 观测点 | 告警投影行 |
| --- | --- |
| phase 2 之后（车载端进程重启过） | `sessionGeneration: 2`，`snapshotSequence: 1` |
| 全程结束（服务端也重启过） | `sessionGeneration: 3`，`snapshotSequence: 2` |

| 断言 | 结果 |
| --- | --- |
| `onboardAlarmProjectionAdoptedTheRestartedVehiclesSnapshot` | `PASS` |
| `onboardAlarmProjectionNeverRegressedToAnEarlierGeneration` | `PASS` |

与 `6dc4bc8` 那一份逐项相同。车重启后告警板序号回到 1，按 `(会话代, 序号)` 仍被采纳；服务端重启之后不回退。

顺带看到的：拒绝路径那一段也照旧走通——phase 2 之后会话代 2、`RecoveryRequired`／`SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`，
phase 3 会话代 3 原因码不变，三个阶段无重连循环。

## 未在本轮证明的

- **看板失联判定本身不在这个 runner 的断言里**，它读库里的快照行、不渲染看板。那一条由 L2
  `onboard-alarm-snapshot-dashboard` 证。
- **车上告警内容仍然是空的**：真车载端 `OnboardAlarmBoard.Raise` 没有调用者。
- **篡改过的车没有被修回来**：`ApprovedSlotHardwareFacts` 写死。
- `protocol-v1.0.0` 这个 tag **尚未打**。本次绑的是 commit。
