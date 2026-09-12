# 现场窗口二（无人）第一次实跑：中止，未 finalize

2026-09-12 13:30–14:02（本地时间），`agv01`，服务端 `403f306`、车载端 `6b8a0b0`、`protocol-v0.3.0`。
授权记录见 [#20 留言](https://github.com/trytoreachpeak0/8005-agv-program/issues/20#issuecomment-5643772543)。

**这个目录是红证据，不是一次完成的窗口。**驱动脚本没有写现场记录，采集器没有 finalize，所以没有 `assertions.json` 与 `SUMMARY.md`。

## 走到了哪一步

| 帧 | 时刻（本地） | 状态 |
| --- | --- | --- |
| `01-00-ready` | 13:30:56 | 预检通过，会话 `Ready` generation 434，两个门 `Off` |
| `02-t-settled` | 13:40:04 | **场景 T 在真车上成立**：停靠 1（`N19-1_N20-1`）没人扫码，期限 13:40:02 到，2 秒后需求 `Cancelled / CANCELLED_BY_STATION_TIMEOUT` 并抑制，旅程自己去停靠 2 |
| `03-x-settled` | 13:42:21 | **场景 X 在真车上成立**：停靠 2（`N13-4_N14-4`）扫码前按「取消装货」，3 秒后以 `CANCELLED_BY_OPERATOR` 结算 |
| `04-stuck-departure-safety-after-x` | 13:5x | 停靠 2 之后永远停在 `AwaitingDepartureSafety / PRE_DEPARTURE_SAFETY_NOT_VALID`——缺陷 [8005-agv-program#52](https://github.com/trytoreachpeak0/8005-agv-program/issues/52)，`docs/defects/20260912-departure-safety-answer-lapses-after-stop-ends-without-load.md` |

13:59 左右车挡路，现场人员把车手动开走。随后停驱动脚本与守望（factory01 上两个守望进程按命令行结束，复查 0 个），
`Set-JourneyRuntime.ps1 -Off`，`10-start-onboard-stack.ps1 -Stop`，`15-set-automation-face.ps1 -Revert`（93 个其他叶子不变）。

## 留下的现场

- 旅程 `bb16f190-fb7d-2958-82f0-9fc67e77d243` 未结束：停靠 1、2 的需求 `Q26091814-1`、`Q26092612-12` 已被**永久抑制**；停靠 3、4 的
  `Q26091208-17`、`Q26092108-38` 仍是 `Planned`。
- 场景 NE、R1、CH、第二趟与 R2 都没有跑到。
- 整窗 L2 彩排 `real-onboard-field-window2-rehearsal-002` 在这一格是绿的：它用 1 秒轮询间隔，漏掉了这个缺陷。
