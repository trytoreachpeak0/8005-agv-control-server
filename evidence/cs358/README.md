# control-server#358 的真装置证据

## 行驶中 FAILED（一次性场景，本机真装置）

目录：`real-onboard-order-failed-in-transit-86773d9d/`

**这不是常驻场景**，没有进 `scripts/l2/scenarios/`，也没有进 `l2.yml`。脚本副本在 `scenario/` 下（加了 `.txt` 后缀，免得被当成场景或被扫描）。复跑方法：把服务端 worktree 的 `scripts/l2/` 与 `scripts/DesktopLock.psm1` 复制到别处，把两个文件去掉 `.txt` 放进副本的 `scripts/l2/scenarios/`，然后运行副本的 `Invoke-L2Scenario.ps1`，显式传 `-Repository`（服务端 worktree）、`-OnboardRepository`、`-SimulatorRepository`。

三端：服务端 `86773d9d685052bc7ba50857304c5ea781cbc1e9`，车载端 `b789c3d4a249cd3368aa53ea5f7ebe992d109666`，模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`。runId `20260926T160359204Z`，结论 PASS。

### 回答了票面的哪个问题

票面把「单 FAILED 之后车还在动，真车载端会话到底就不就绪」列为推的、没核过。这一轮的记录（`timeline.jsonl` 里的 `session-*` 三行）：

| 时刻 | 会话 |
| --- | --- |
| 注入 FAILED 之前（车在途） | `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY` |
| 服务端记下故障的那一刻 | `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY` |
| 之后 4 轮 | `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY` |

所以真车载端在这种状态下会话一直未就绪，故障与急停只能由会话闸门后那条路发出。修后的服务端在注入后约 1 秒记下故障（`SuspectedBlocked`、`VEHICLE_ORDER_FAILED`），同一轮发出 `OrderHold` 与急停触发，旅程码是 `VEHICLE_ORDER_FAILED`。

### 命令审计（`snapshots/db-RiotOrderCommandAudit.json`）

| 命令 | 次 | 结果 | 时刻（UTC） |
| --- | --- | --- | --- |
| `OrderHold` | 1 | `Failed` | 16:04:27.86 |
| `triggerEmergency` | 1 | `Pending` | 16:04:27.99 |
| `triggerEmergency` | 2 | `Pending` | 16:04:30.79 |

`OrderHold` 的结果是 `Failed`：对一张已经 FAILED 的单，对账读回的就是「终态、不是想要的那个」，按设计不再重发。第二次急停触发是急停监督在触发未确认时的受控退避重试（初始间隔 2 秒），因为假 RIoT 这一轮从头到尾报 `emergencyState=OK`、没有模拟锁上；不是 REQ-0248 的重触发。

### 没放进来的

`logs/` 与其余库快照留在本机 `C:\w2g\cs358-rig\86773d9d-real-onboard-order-failed-in-transit-001`，没有入库：这次判据只用得到上面几份。

## CI 真装置

`real-onboard-compensate-then-reconnect` ×1，run `36254102300`，服务端 `86773d9d`、车载端 `b789c3d4`、模拟器 `fb5f7c59`（从场景那一步读），`PASS, 70s`；`RIG_COMMIT_GUARD`、`RIG_DESKTOP_LOCK`、`RIG_DEADLINE`、`NOT_STARTED` 在源码回显之外 0 次。证据在那次 run 的 artifact 里。
