# cs#306 证据：staged runner 在当前顶端判 FAIL

票：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/306

## 结论

不是产品回归。runner 两处过时，其余是连锁：

1. runner 把车载端 `wireToGate.messageTimeoutMs` 钉成 3000；onboard-hmi#142（`429e0ff`，在 `deeba94c`、不在 `4d716340`）起车载端要求它严格小于 3000，启动时拒收配置，从没连上。
2. 槽位配置激活 409 是连锁：服务端没有这辆车的会话，回 `No session for this vehicle`。
3. 重连后重放 FAIL：连锁（为真实车载端预备的一次性丢 ack 规则落到恢复探针上），外加第二处过时——control-server#202（`1e404804`）起握手完成才发恢复命令，探针那条连接不发恢复报告。
4. runner 错误路径把原错误盖成 `IndexOf` 空参数异常。

## 目录

| 目录 | 内容 |
| --- | --- |
| `red/error-path-selfcheck/` | `Test-StagedG3ErrorPath.ps1` 在未修的 runner（`3c930c8f`）上：6 条红 |
| `green/error-path-selfcheck/` | 修后 25 条全绿 |
| `red/error-path-mutations/` | 对修后 runner 的三处变异，各只红预期的那一两条 |
| `staged/a-before-fix/` | A 轮：只带错误路径修复、未修过时，当前顶端复现 |
| `staged/b-tip-fixed/` | B 轮：修后，当前顶端 |
| `staged/c-shared-binding/` | C 轮：修后，共享绑定不动 |

## staged 三轮（本机时段 2026-09-22 08:42–08:55，各一遍）

各端提交读自每轮 `run-result.json` 的 `commits`，与各轮 `logs/checkout-*.log` 一致。模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`、协议 `86575456c847041515b7b75e8851a00e0d939804` 三轮相同。

| 轮 | runner（harness） | 服务端 | 车载端 | 结果 |
| --- | --- | --- | --- | --- |
| A | `7a0c11efd55af392a2e19dad08be6717496f5311`（临时，未推送：`d6df6fed` + 移绑定） | `d6df6fede43c0ee2a8d57e2567bb0af84ceca60b` | `deeba94c42c51561621634194d3c9f6582737486` | `INCONCLUSIVE_RUNNER_ERROR`，29 条判据 10 PASS |
| B | `fb66b523599a4808da5a6b8258bbdec7c066e346`（临时，未推送：`8a4d98f9` + 移绑定） | `8a4d98f90f708563f41f90a35d98cd9e932ca94c` | `deeba94c42c51561621634194d3c9f6582737486` | `STAGED_G3_RECOVERY_REPLAY_PASS`，29/29 |
| C | `8a4d98f90f708563f41f90a35d98cd9e932ca94c` | `85381ea2a37e46b4c720ff5f1843161ad6deb69d` | `4d716340982de4e39339c2151c291efe1a21e1d1`（detached worktree，`-OnboardRemoteRef` 传 sha） | `STAGED_G3_RECOVERY_REPLAY_PASS`，29/29 |

A、B 的服务端 `src/` 等于 `fp/v2-impl@3c930c8f`；C 的等于共享绑定 `85381ea2`。

**临时提交与它所基于的提交只差绑定两行。**`git diff --stat 8a4d98f9 fb66b523` 是 `scripts/run-staged-g3.ps1 | 4 ++--`，两处都在 `param` 块的默认值上：`$ControlServerCommit` 由 `85381ea2…` 改为 `8a4d98f9…`（让 runner 克隆 PR head 本身当服务端），`$OnboardCommit` 由 `4d716340…` 改为 `deeba94c…`（`origin/w2g/fp-v2-impl` 当前顶端）。判据、错误路径、探针都没碰，所以 B 是 head `8a4d98f9` 在当前顶端上的证据。完整 diff 在 `b-tip-fixed/harness-vs-head.diff.txt`；A 的临时提交与 `d6df6fed` 同样只差这两行，只是 `$ControlServerCommit` 指 `d6df6fed`（`a-before-fix/harness-vs-base.diff.txt`）。

### A 轮读到了什么

- `a-before-fix/slot-configuration-activation-observation.json`：`issueHttpStatusCode` 409，`issueHttpResponseBody` 的 `title` 是 `No session for this vehicle`，`detail` 是 `No session has ever been established for 'AGV-8005-STAGED-G3-01'.`
- `a-before-fix/fault-proxy-events.ndjson`：真实车载端的故障代理整轮只有一行 `proxy-listening`，没有任何连接。车载端 `onboard.out.log`、`onboard.err.log` 都是 0 字节（完整证据目录见下）。
- `a-before-fix/runner-error.json`：原来被盖住的错误，`System.IO.EndOfStreamException: Expected a RecoveryActionRejected response.`，出自 `RunRecoveryProbeAsync`。
- `a-before-fix/onboard-config-offline-load.txt`：**这一条不是运行中读到的。**车载端在日志器建立之前拒收配置，运行证据里按构造不会有这条报错。所以在 A 这一轮自己的 stage 上（`C:\g3306a\publish\onboard-hmi`：`deeba94c` 的发布产物，加上 runner 写进去的 `appsettings.json`，其中 `messageTimeoutMs` 为 3000）用 `probe-onboard-config.ps1` 只加载 `SQCD.Agv.Infrastructure.dll` 调 `OnboardSettings.Load`，不起窗口：

  ```
  LOAD_REFUSED System.IO.InvalidDataException: WIRE_TO_GATE消息超时必须严格小于ADR-cross-0027静默失联阈值的一半（3000毫秒）：心跳要等应答才算走完一拍，这个超时就是两条心跳到达间距的上界。
  ```

  意思是：车载端的配置校验认为消息超时 3000 毫秒不合格（必须严格小于 3000），于是拒绝启动。

### B、C 轮

两轮 29 条判据全部 PASS：`recoveryFaultInjection.droppedCommandCount` 1、`replayedCommandCount` 1（强制恢复命令确实被规则丢掉、并在重连后重放），真实车载端的恢复报告 ack 被丢 1 次，激活受理为 `PENDING_RESULT`、车回报 1 次。摘要在各目录的 `run-summary.json`。

## 完整证据在哪

三轮完整证据目录在控制端 `C:\Users\szy\b306\{a,b,c}`，stage 根在 `C:\g3306a`、`C:\g3306b`、`C:\g3306c`，不入库（G3 自检证据的一贯做法）。09-22 那一轮的原始材料 `C:\Users\szy\b715\sc-staged\` 与 `C:\g3b715s` 只读，未改动。
