# cs#230 绿证据：`real-onboard-restart-after-recovery-session-opened` 清单三连

CI `rig=real` run [`35491230857`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35491230857)，
`mode=consecutive-all`，三遍全 PASS，每遍九条判据全 PASS。

| 遍 | 结果 | 秒 |
| --- | --- | --- |
| `-01` | PASS | 45 |
| `-02` | PASS | 41 |
| `-03` | PASS | 42 |

三端提交（从作业日志 `Run real-onboard L2 scenarios` 那一行读回，并与 `commits.json` 对上——`workflow_dispatch`
的 inputs 不在 API 元数据里，只能这么核）：

| 端 | 提交 | 与集成分支顶端的关系 |
| --- | --- | --- |
| control-server | `769985eb` | 本票分支，已 merge `fp/v2-impl` 顶端 `ce9bce83`（cs#208） |
| 8005-agv-onboard-hmi | `dc1f033b` | `w2g/fp-v2-impl` 当时的顶端 |
| slots-simulator | `fb5f7c59` | `main` 当时的顶端 |

整机已提交内存：最低 3.78 GiB、峰值 6.62 GiB（68 次采样，`commit-samples.csv`），上限 16 GiB。

## 这三遍证了什么

装载以 `UNKNOWN` 收尾之后按「补偿清空」，恢复会话正常开成、动作也被服务端受理，而受理的应答被协议故障代理
丢掉；车载端已经把会话 id 与动作向量落了盘，等满 `messageTimeoutMs` 报失败。此时断电重启——

- 服务端把那份 `ACTION_SELECTED` r2 快照重放进新会话（同 messageId 改绑新代次、载荷不变）；
- 车载端的恢复入口可用，**第一次按下就走到补偿结果**，没有在本地抛 `RECOVERY_SESSION_STATE_PENDING`；
- 不重开会话，`ExceptionRecoverySessionRequested` 在重启后为 0，这辆车始终只有那一个会话行；
- 恢复动作**全程只提交过一次**（重启前那次）——重启后走的是「快照里已选中同一动作」的早退分支，
  直接续发 `LoadCompensationRequested`；
- 补偿 `ALL_EMPTY`，走到对账：工作流 `Reconciled`、会话 `CLOSED`、需求与装载 `Cancelled`、租约释放、
  旅程 `Completed/CANCELLED_BY_LOAD_COMPENSATION`、会话回 `Ready`，仓位关门空锁、补偿全程没开锁。

## 顺带的跨端回归

对端 `dc1f033b` 含 onboard-hmi#152（恢复投影改成「只在没有别人持有显示时才带快照」）。重启之后 display owner
是 `null`，而那条投影是把恢复入口放上屏幕的唯一路径——它自己的注释点名了这一点。本场景第 7 步正压在上面，
三遍都如期出现入口。hmi#152 自己那遍真装置跑的是 `compensate-then-reconnect`（断链重连，owner 不为 null），
没覆盖到这一格。

## 对照的红

注入故障（丢掉重启后重放的快照）后的红在 `evidence/l2/20260920-cs230-red-snapshot-replay-dropped/`：
`L2-RAO-06` 红、`-01`～`-05` 仍绿、`-07`～`-09` 未到达。

## 首跑

run [`35490679217`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35490679217)，
PASS，46 秒，服务端还是 `fe7f0154`（未含 cs#208）。那一遍是调试用的，最终证据以本目录这三遍为准。
