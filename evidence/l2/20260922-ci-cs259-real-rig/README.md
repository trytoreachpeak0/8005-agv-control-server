# CI 真装置：control-server#259（PR #309）

两次 CI 真装置运行，都在服务端 `7baf1e64` 上：

| run | 场景 | 模式 | 结果 |
| --- | --- | --- | --- |
| [`35679117127`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35679117127) | `real-onboard-durable-ack-lost` | `consecutive-all` × 3 | `-01` FAIL (1)，`-02`、`-03` PASS |
| [`35679127069`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35679127069) | `real-onboard-compensate-then-reconnect` | × 1 | PASS |

之后分支上只多了一个提交 `2e435788`，它只改 `OnboardPeer.cs` 里的 `///` 注释（非注释的改动行数为 0，调度已核对），所以这两次的证据沿用到 `2e435788`。

## 四行核对（两次相同）

| | |
| --- | --- |
| 服务端 | `7baf1e64a66dee213075adc74149eeba48b3b8a0` |
| 车载端 | `deeba94c42c51561621634194d3c9f6582737486`（`w2g/fp-v2-impl` 顶端） |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端） |
| 真跑起来了 | `RIG_COMMIT_GUARD`／`RIG_DESKTOP_LOCK`／`RIG_DEADLINE` 每个 run 只命中 1 次，而且都在带 `^[[36;1m` 的脚本源码回显行里，没有真正触发的停止 |

三端提交取自「Run real-onboard L2 scenarios」这一步打印的身份，也就是各目录下的 `commits.json`。

## 本票的判据：三遍都绿

- 「补发之后同一条连接照常走完握手」三遍都是 PASS。第 2 条连接的握手按序走完（`SessionHello → SessionAccepted → OperationResult → DurableAck → CapabilitySnapshot → … → RecoveryStateReport → DurableAck → SessionReadiness`），中间没有插进任何推送。这正是本票要堵的窗口。
- 会话判回 `gen 2 / Ready / READY`，三遍都 PASS。
- 「补发之后没有哪条连接被服务端掐掉」三遍都 PASS。

## `-01` 的红：记为 cs#307 的第二例，不算本票回归

```
FAIL 丢一次 ack 只换来一次重连  实际: 3 connections, 1 open (#1: relay dropped DurableAck for OperationResult; #2: onboard closed; #3: open)
```

判断依据，按强度从高到低排：

1. **本票新加的两道拒绝在这一遍里一次都没触发。** 服务端日志里 `No recovered Onboard peer` 0 次，代次比对的 `built for generation` 0 次，`2002` 也是 0 次。所以这次断开走的不是本票改动的路径。
2. **本票自己的判据在三遍里全绿**，见上一节。
3. 同一形状在修改之前的代码上已经出现过（run `35516706603`，`evidence/l2/20260920-ci-35516706603-cs204-final/`）。这一条单独拿出来，只能说明这种停顿早就存在，推不出「本票没让它更常见」。撑起结论的主要是第 1 条。

**读到的**（`real-onboard-durable-ack-lost-01/snapshots/protocol-fault-proxy.json`、服务端与车载端日志，时刻为 UTC+8）：

- 第 2 条连接在 10:25:03.959 完成握手（回出 `SessionReadiness`），此后正常收发大约 42 秒。服务端在 10:25:46.555 回完 `HeartbeatAck`，之后在这条连接上再没回过任何报文：`OperationResult`（46.792）、`Heartbeat`（48.095）、补发的同一条 `OperationResult`（49.319）都没有应答。
- 车载端 10:25:49.30 记 `OperationResult暂未收到DurableAck ... TimeoutException: The operation has timed out.`，随后自己断开（`#2: onboard closed`）。
- 第 3 条连接上，同一条 `OperationResult` 的 `DurableAck` 用了 2.2 秒（53.836→56.036），`RecoveryStateReport` 的应答用了 1.3 秒。
- 同一时段服务端日志里 `GET /api/onboard/v1/vehicle-safety` 要 0.5～1.2 秒。这一遍开始时，整机已提交内存 9.81 GiB。

**推出来的，没有直接证据**：服务端这条连接的处理循环在处理 `OperationResult` 时卡住超过 2.5 秒（车载端的等待超时），原因可能是 SQLite 争用或机器负载。服务端那条 `SocketException (10053)` 是车先断开之后服务端读 socket 时报的，是结果，不是原因。

## `-02`、`-03` 里的推送被拒

`-02` 有 2 次 `No recovered Onboard peer`，`-03` 有 3 次，每次都记一条 `2002`。代次比对都是 0 次。

- `-02` 的两次都在 10:27:21～22，在丢 ack、断线之后、`SessionHello`（23.352）之前，属于普通断线期间。
- `-03` 前两次在断线期间。第三次在 10:29:04，与握手（04.815～05.530）落在同一秒，秒级日志分不清它是断线期的末尾，还是被握手窗口挡下的。

两种情况的处理相同：报文留在发件箱，下一轮补发。旅程都照常走完（「补发被确认之后旅程照常走完」PASS）。

## 这里存了什么

按调度的清单：每一遍的 `SUMMARY.md` 与 `assertions.json`；红的那一遍另加代理快照 `snapshots/protocol-fault-proxy.json` 和收发时间线 `timeline.jsonl`；每个 run 的汇总 `SUMMARY.md` 与 `commits.json`。服务端的 SQL 日志和其余 artifact 不入库，按上面的 run id 去 Actions 下载。

L2 PASS 仍然**不代表真实 RCS、真车、真实 IO 模块或接线合格**。模拟器只证明软件 IO 闭环。
