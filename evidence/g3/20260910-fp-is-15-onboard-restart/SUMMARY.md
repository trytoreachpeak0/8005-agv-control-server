# `G3`（进程重启）：`FP-IS-15` 的另一半 **`PASS`**

`STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT`，`assuranceLevel` 为 `STAGED_REBUILD`，
22 条断言全绿，`FP-IS-00` / `FP-IS-06` / `FP-IS-15` 三份 `gate-result.json` 的
`formalSlicePass` 都是 `true`。

## 它补上了哪一半

同日 staged runner 的那一轮把 `FP-IS-15` 证到「投影那一行带着它到达时的会话代」为止。
**「车重启后告警序号回到 1 仍被采纳」证不到**——那个 runner 不重启车载端进程。这一份补上它。

判据取 `controlDatabaseBeforeServerRestart`：它是在 phase 2 之后、phase 3 的服务端停掉时读的，
正好是「车载端刚重启完」那个状态。实读：

| 观测点 | 投影行 |
| --- | --- |
| phase 2 之后（车载端进程重启过） | `sessionGeneration: 2`，`snapshotSequence: 1` |
| 全程结束（服务端也重启过） | `sessionGeneration: 3`，`snapshotSequence: 2` |

三个阶段的会话代分别是 1 / 2 / 3。

**读到「代 2、序号 1」这件事本身就是证明。**phase 1 那一份的序号也是 1——新进程的告警板从 1 开始
数——而 1 不前进过 1。所以一条纯序号的采纳规则会忽略重启后那份快照，把投影留在代 1，看板就此停在
车重启前那一批，正是 REQ-0269 要禁的不确定新旧的旧值。

第二行也有它的用处：服务端重启之后代到了 3、序号变成 2（车载端这一次没重启，同一个进程的告警板
继续往下数），说明采纳既不因为服务端换了进程而回退，也没有把序号的推进算成一次新的会话。

## 两条断言

| 断言 | 结果 |
| --- | --- |
| `onboardAlarmProjectionAdoptedTheRestartedVehiclesSnapshot` | `PASS` |
| `onboardAlarmProjectionNeverRegressedToAnEarlierGeneration` | `PASS` |

其余二十条是这个 runner 原有的恢复、身份与持久化断言，本轮一并复跑，未见回归。

## 绑定

commit 绑定不是这个 runner 自己写的，它从 `scripts/run-staged-g3.ps1` 的 param 块读回来
（`commitBindingSharedWithMainRunner` 就是在断言这件事），本轮同时把 `OnboardRemoteRef` 也改成
从同一处读——分支名原本是这里硬编码的第二份拷贝。

| | commit |
| --- | --- |
| `controlServer` | `b6064690799aaf374d00f1bbde43228d8c3c60bd` |
| `onboard` | `afba86e07116192bde386937c559cc7a70a00a7f`（`origin/w2g/b3-on-v2`） |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |

车载端与模拟器这一次仍是从本地仓克隆的（`-OnboardRepository`／`-SimulatorRepository` 指向本地），
但 `w2g/b3-on-v2` **已经推送到远端**，`afba86e0` 在 `origin/w2g/b3-on-v2` 上，与本地那一份是同一个
commit。这与同日更早那两份 G3 不同，那时这条分支还没推。

## 未在本轮证明的

- **`FP-IS-14` 不在这个 runner 的 claim 里。**激活那条路由 staged runner 覆盖，见
  `../20260910-fp-is-14-15-activation-and-alarm-retry/`。
- **告警内容仍然是空的。**这一次同样没有真实告警条目进入快照，所以带非空内容的投影、以及
  「后一份整体取代前一份」在 `G3` 这一层依旧没有证到。
- `protocol-v1.0.0` 这个 tag **尚未打**，attestation 是 `PENDING` / `TRACKED_TEMPLATE`。
  本次绑的是 commit，不是 tag。
