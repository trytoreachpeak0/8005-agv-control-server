# `G3`（进程重启）：`FP-IS-00`／`06`／`14`／`15` **全部 `PASS`**（服务端 `6369616`，协议 `16e2567`）

`STAGED_G3_PROCESS_RESTART_PASS`，25 条断言全绿，`failedAssertions` 为空。本轮**没有带 `-Slice`**，四片各写一份 `gate-result.json`，
都是 `formalSlicePass: true`。

**它取代以下证据，作为进程重启 runner 的现行证据：**

- `FP-IS-00`、`FP-IS-06`、`FP-IS-14`（拒绝路径）：`../20260910-fp-is-14-fingerprint-mismatch-mapped/`
- `FP-IS-15`（车重启后告警采纳）：`../20260910-fp-is-15-onboard-restart-eefb3a8/`
- 同日首跑 `../20260912-fp-is-14-15-restart-6369616/`：那一份四片也是 `PASS`，但 `runnerWorktreeCleanAtStart: false`，
  因为主 runner 刚把证据写进了工作树。不当现行证据。

以上几份都原样保留、未改一字。重跑的原因与同日主 runner 相同，见 `../20260912-fp-is-14-15-staged-harness-58f1a49/SUMMARY.md`。

## 绑定

commit 绑定照旧从 `scripts/run-staged-g3.ps1` 的 param 块读回（`17cf478` 移过来的）。

| | commit |
| --- | --- |
| `controlServer` | `63696161d036a4907a39f8597fd64cc6e4c755fd`，三个阶段的 `serverBuildCommits` 一致 |
| `onboardHmi` | `f9efa301734128e850cb5460c20265232611ffff`，三个阶段的 `onboardBuildCommits` 一致 |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `16e2567a7033883f00fc999f7fa08f954dd13a26` |
| `runner` | `0070886870992995bd71d13438ef114962336bd0`，`runnerWorktreeCleanAtStart: true` |

**目录名写的是 `58f1a49`，runner 记的是 `0070886`，两者是同一份 runner 代码。**`0070886` 只是在 `58f1a49` 之上提交了主 runner
的证据：跑之前核过 `git diff 58f1a49 0070886 -- scripts src tests` 为空。先提交主 runner 的证据，就是为了让这一轮开始时工作树干净。

## 实读

**`FP-IS-14` 拒绝路径**，与 `6dc4bc8` 那一份结论相同：

| 时刻 | 库里的事实 |
| --- | --- |
| phase 1 | 车与已批准那一版一致，激活 `ACTIVATED`，version 2（对照组） |
| phase 2（篡改车上生效配置，车载端进程重启） | 会话代 2，`RecoveryRequired`／`SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`；第二次激活被车拒绝，激活行 `FAILED`，version 3 |
| phase 3（服务端重启） | 会话代 3，原因码不变；生效配置仍是 version 2 |

**`FP-IS-15` 车重启后告警采纳：**

| 观测点 | 告警投影行 |
| --- | --- |
| phase 2 之后（车载端进程重启过） | `sessionGeneration: 2`，`snapshotSequence: 3` |
| 全程结束（服务端也重启过） | `sessionGeneration: 3`，`snapshotSequence: 5` |

| 断言 | 结果 |
| --- | --- |
| `onboardAlarmProjectionAdoptedTheRestartedVehiclesSnapshot` | `PASS` |
| `onboardAlarmProjectionNeverRegressedToAnEarlierGeneration` | `PASS` |

**序号与 `eefb3a8` 那一份不同，要读清楚。**那一份两个观测点分别是序号 1 和 2；这一份是 3 和 5。原因是车载端 `a98679f` 之后
会在会话中途上报真实的告警变化，每一代里就不止握手那一份。采纳断言比的是会话代：phase 2 之后投影行的会话代是 2，只有重启后的
车载端进程才会产生这一代，而它的告警板序号从 1 开始。所以序号回到 1 的快照确实被采纳了，只是到了观测点，已经被同一代里的
序号 2、3 顶替。**这是推理，不是本轮直接观测到序号 1。**

**真进程之间出现了两种告警。**跑完之后，stage 目录里服务端库的投影行告警如下：

- `ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE`，11:33:06 抬起：staged 环境里出发安全信号是 `DISABLED`；
- `ONBOARD_SLOT_CONFIGURATION_MISMATCH`，11:33:24 抬起：phase 2 篡改车上生效配置后，会话原因码带上了指纹不符，车载端据此抬起。

后一条是第一次在真进程之间出现，同日主 runner 只看到前一条。

恢复报告的 `messageId` 在车载端 journal 与服务端 inbox 里逐一对应，三个阶段没有重连循环。

## 未在本轮证明的

- **看板渲染不在这个 runner 里。**它读库里的快照行，不渲染看板页面；看板显示全部告警由 L2 `onboard-alarm-snapshot-dashboard`
  证明（CI `34690440914`）。
- **篡改过的车没有被修回来。**恢复规程 `docs/field/tampered-slot-configuration-recovery.md` 还没有门禁阶段。
- 其余告警条件（IO 离线、锁反馈丢失、行驶中仓门未锁等）没有在真进程之间出现过。
- `protocol-v1.0.0` 这个 tag **尚未打**，没有任何人签过 attestation。
