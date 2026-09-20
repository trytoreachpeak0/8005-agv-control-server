# cs#230 红证据：丢掉重启后重放的会话快照，场景红在「恢复入口不可用」那一步

CI `rig=real` run [`35490938927`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35490938927)，
一遍，FAIL(1)，55 秒。三端提交（从 `commits.json` 与作业日志读回，不是从 workflow 元数据猜的）：

| 端 | 提交 |
| --- | --- |
| control-server | `1157e522`（注入版，下一个提交已 revert） |
| 8005-agv-onboard-hmi | `dc1f033b` |
| slots-simulator | `fb5f7c59` |

## 注入了什么

在重启车载端之前布下 `PUT /control/v1/drop-message { messageType = 'ExceptionRecoverySessionSnapshot', count = 5 }`，
丢掉服务端在重启之后重放给车载端的那份会话快照。注入版脚本存在本目录 `injected-scenario.ps1`，与正式场景只差
那一段（正式场景里没有）。

**为什么布在重启之前而不是之后。**到这一步，这个恢复会话的每一份快照都已经发过了，能被丢掉的只剩重放的那些，
所以效果与「重启后立刻布」相同，而且不必和车载端的重连抢时间——没有竞态，红不红不取决于机器快慢。

## 红在哪一步，以及为什么 `L2-RAO-05` 是绿的

| 判据 | 结果 | 实际值 |
| --- | --- | --- |
| `L2-RAO-01` 装载 UNKNOWN 的前提 | PASS | UNKNOWN / RecoveryRequired / Blocked |
| `L2-RAO-02` 会话开成、动作受理、受理应答被丢 | PASS | 会话行 1：ACTION_SELECTED、COMPENSATE_LOAD_ALL_EMPTY、r2；工作流 1：AwaitingAuthorization、无命令 |
| `L2-RAO-03` 车载端日志库里已有会话 id 与向量 | PASS | 会话 `5f9fb4b9…`、动作 `9c2eea80…`、向量 `LOAD_COMPENSATION` |
| `L2-RAO-04` 重启前恰一份待重放快照、r1 已 fence | PASS | r1 OPEN fenced=True → r2 ACTION_SELECTED fenced=False |
| `L2-RAO-05` 服务端把它重放进新会话 | **PASS（有意）** | 同一行改绑 g3、载荷相同、仍未确认未 fence |
| `L2-RAO-06` 重启后恢复入口可用 | **FAIL** | 第一次按下：REFUSED；再按一次：REFUSED |
| `L2-RAO-07` 动作只提交过一次、补偿走完 | FAIL（未到达） | 重启后「补偿清空」没有走到 LoadCompensationResult |
| `L2-RAO-08` 走到对账、车回 Ready | FAIL（未到达） | 同上 |
| `L2-RAO-09` 开着的快照从没被确认 | FAIL（未到达） | 同上 |

**`L2-RAO-05` 绿是这份证据的一部分，不是漏网。**服务端的 `ReplayPendingForSessionAsync` 先把发件箱那一行改绑到
新代次、写库，然后才发出去；代理丢的是网络上那一行。所以「服务端做了重放」这个事实照样成立，而 `L2-RAO-05`
读的正是服务端发件箱。不成立的是下一件事——车载端因此拿回了会话——那是 `L2-RAO-06` 判的。两条判据分开，
正是为了让这种情形能把责任落到具体一端。

**`L2-RAO-06` 红得对。**车载端没有收到快照，内存里没有活动会话，而日志库里有动作向量，于是恢复入口在**本地**
就以 `RECOVERY_SESSION_STATE_PENDING` 拒绝（车载端 `WireToGateBusinessService.RecoveryVectors.cs:963`），
一条消息都没发出去——所以「重启后 SessionRequested 0」这一半仍然成立，红的是「第一次按下就走到补偿结果」那一半。

**「再按一次也 REFUSED」是有意判的。**场景在第一次被拒后等 10 秒再按一次，只为区分「一时的」与「一直的」；
判据 `L2-RAO-06` 只看第一次按下，第二次的结果抄在实际值里。两次都 REFUSED 说明这不是竞态。

## 对照的绿

同一个场景、同一对端、未注入时的绿在 run
[`35490679217`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35490679217)（PASS，46 秒，
服务端 `fe7f0154`）与清单三连（见 PR）。
