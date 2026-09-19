# control-server#222 证据：real-onboard-restart-with-open-recovery-session 稳定红的定论与修复

三端：服务端本票分支，车载端 `4d716340`（除 `red/35454377282-onboard-579f19c` 外），模拟器 `fb5f7c59`，协议 `protocol-v2.0.0`。
全部在 vm01 的 CI 真装置作业（`l2.yml`，`rig=real`）上跑。更早的两次红（车载端 `4d716340`）已在
`evidence/l2/20260919-ci-real-rig-runs/35450443032`、`35451359668`，不重取。

## red/

| 目录 | 服务端 | 内容 | 结论 |
| --- | --- | --- | --- |
| `35454377282-onboard-579f19c` | `e42c1bd7`（= `fp/v2-impl` 675f9fd9 + 清单一行） | 二分左端：车载端换成 v2 一侧第一个合并 `579f19c` | 与 `4d716340` 完全相同地红在 L2-ROS-02（入口未出现）：v2 一侧没有拐点 |
| `35455541316-handle-fixed-old-premise` | `e5ef8e44`（只补了重取句柄） | 入口出现并按下 | 服务端**接受**了 RESUME_AFTER_REPAIR（会话 CLOSED、r3、工作流 1），旧前提「重启前续行被拒、会话 OPEN」不成立，L2-ROS-02 红 |
| `35456298574-inject` | `9044c361` | 两个临时注入变体 | `cs222-inject-session-answer-not-dropped`（不丢开会话应答）：**前提自检**，装置前提不成立，L2-ROS-02～08 全部未到达，不是产品缺陷红。`cs222-inject-replay-dropped`（只丢一条重放）：全绿——服务端在新会话里重放了两次，丢一条不够，见下一行 |
| `35456567497-inject-replay-dropped` | `25acaf3b`（丢十条） | 重启后 OPEN 快照的重放全部到不了车 | L2-ROS-05 红：两次按「补偿清空」都另申请会话、被 `ExceptionRecoverySessionRejected`，正是 control-server#36 要防的断路 |

## green/

| 目录 | 服务端 | 结论 |
| --- | --- | --- |
| `35456039003` | `037ddc06`（改写后） | 8/8 PASS |
| `35456809964-consecutive-all` | `c21eca0b`（放回清单后） | 三连 3/3 PASS（47／40／41 秒） |
| `35457297863-consecutive-all-after-merge` | `341d40a0`（合入 `fp/v2-impl` 3dc7a431，含 cs#202 握手中不发恢复快照） | 三连 3/3 PASS（66／47／43 秒，整机已提交峰值 11.35 GiB，有别的作业同时在跑） |

## selfcheck/

`scripts/l2/Test-L2OnboardHandleAfterRestart.ps1` 的输出：`red-d10b0230.txt`（测试提交、修复前，报本场景 3 处）、
`red-fa8fd1bc.txt`（cs#128 那一提交的场景目录，同样 3 处）、`green-head.txt`（修复后 62 个场景无报）。

## onboard-probe/

车载端一次性 G2 探针的补丁与输出，说明车载端本该给出「申请恢复」。补丁没有提交到车载端仓。
