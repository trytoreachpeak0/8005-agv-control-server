# 2026-09-09 protocol-v0.3.0 身份下的 staged G3：两个 runner 通过，第三个被一道刻意的护栏挡住

## 结论

| runner | 运行 ID | 结果 | 断言 |
| --- | --- | --- | --- |
| `run-staged-g3.ps1` | `20260909T045648359Z` | `STAGED_G3_RECOVERY_REPLAY_PASS` | 19 / 19 |
| `run-staged-g3-restart.ps1` | `20260909T045823049Z` | `STAGED_G3_PROCESS_RESTART_PASS` | 20 / 20 |
| `run-demand-bearing-g3-vectors.ps1` | `20260909T045951118Z` | **`INCONCLUSIVE_RUNNER_ERROR`** | 7 / 20 |

三个 `status` 与全部断言值都是 runner **自身发射**的机器可读结果，落在各自的 `run-result.json` 里，
本文档只是转述，不是判定来源。三次 `secretLeakFiles` 均为空数组。

## 冻结身份

| 组件 | commit | 来源 |
| --- | --- | --- |
| ControlServer | `8b554bea6a2f8ea3d647695625f3de0d81dac016` | 当时的 `ControlServer_MVP` |
| OnboardHmi | `77d5833b32976665ec05a7cb3a9e7a973db887fd` | 当时的 `origin/OnboardHmi_MVP`（PR #22 合入后） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` | 当前 `origin/main`，未变 |
| protocol | `345c53c58517968192c87c3e7777ed08ddb48726` | tag `protocol-v0.3.0` |
| harness | `dc0e0f86819c5c8c5741cd37b045f2861644ca90` | 三个 runner 同一份 |

`run-staged-g3.ps1` 的 `harnessWorktreeCleanAtStart` 为 `true`。另两个是 `false`，原因与
2026-09-04 那轮完全相同：前一个 runner 的证据目录就在本仓库里，对后一个来说是未跟踪内容。

## 开跑前修的三件事

**四个 commit 绑定全部过期**（`8ffcfa1`）。`$ProtocolCommit` 停在 `1531489e`，那是
**protocol-v0.1.1**。不改就是拿两个版本前的协议门禁今天的代码，而且门禁会照常绿。

**协议身份在 `run-staged-g3.ps1` 里有两份**（`59a3c38`）。改完绑定第一次跑就
`INCONCLUSIVE_RUNNER_ERROR`：`protocol-v0.1.1 resolves to 1531489e…, expected 345c53c…`。
PowerShell 侧的 `$protocolTag` 与三个哈希是一份，嵌入的 C# 合成对端里的 `Protocol` 常量类与
`SessionHello` 的 `["tag"]`／`["protocolVersion"]` 是另一份——后者活在 `@'...'@` 里，
**那种 here-string 不插值**，所以它不可能引用前者，只能是手抄的第二份。两份一起抬到 0.3.0，
并加了一条守卫：here-string 结束后把六个值读回来比对，不一致就 throw 并指名道姓。
它的价值在于失败得早——原来那条错误是 `New-ExactClone` 抛的，那时四个仓已经克隆完。

**需求承载 runner 的 baseline 探针钉死了新列名**（`99fee53` + `dc0e0f8`）。
`VehicleDispatchLeases` 的键列在 `20260908042817_MultiDemandJourneyStopSequence` 里由
`DemandId` 改名为 `JourneyId`，而这个 runner 读 baseline 的时机在服务启动**之前**，
那时恢复进来的现场库还没被任何构建打开过。改为 `PRAGMA table_info` 探测，两个名字里恰好
present 一个才继续。第一版探测写错了——`Invoke-SqliteRows` 结尾的 `return ,$rows` 让直接
接管道的调用拿到数组本身而非它的行，改成先赋值再进管道后修复。

## 第三个 runner 为什么跑不了：这不是 bug，是两个要求互相排斥

修完探针，baseline 读通了（四条 RIoT 对账断言从 `FAIL_OR_INCONCLUSIVE` 转为 `PASS`），
卡点换成了服务端起不来：

```
Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005):
SQLite Error 19: 'CHECK constraint failed: SettleEveryJourneyBeforeMigratingToProtocol_0_2_0'.
```

`run-result.json` 里记的 `error` 是 `Timed out waiting for http://127.0.0.1:58307/version`——
那是症状。真因在 `logs/control-1.err.log` 里，上面这条。

这道护栏是 `20260908042817_MultiDemandJourneyStopSequence` 刻意加的，它自己的注释说明了理由：
确定性 messageId 随多单多段改变（一个停靠的工单现在按 stop 与 round 键控），**在途 journey 迁移
之后无法重放自己未确认的消息，而且失败是静默的**。它随 protocol 0.2.0 发布，那是两端一起装的
breaking 版本，所以「有一刻没有任何 journey 在途」是构造出来的。

于是两个要求撞上了：

| 谁 | 要求 |
| --- | --- |
| 迁移护栏 | `JourneyRuntimes` 里**不能有** `Stage <> 'Completed'` 的行 |
| `run-demand-bearing-g3-vectors.ps1`（376 行） | 恢复的库必须**恰好有一个 `Prepared`** 的 station operation |

一个库有 `Prepared` 的 station operation，几乎必然对应一趟在途 journey——而这个 runner 的整套
断言（对一个 Prepared 的操作提交首个结果、重放、拒绝同 id 异内容、拒绝越代结果）正是建立在
那个在途操作上的。

实测 `C:\Users\szy\w2g-stage\run\` 下 32 份现场库，带 journey 行的 19 份：

| 库 | 在途 | station operations |
| --- | --- | --- |
| `fullloop-20260829T131549Z`（本轮所用，交接文档点名的那份） | 1（`AwaitingUnloadResult`） | Committed 1 + **Prepared 1** |
| `gen3-20260829T222602Z`（唯一一份已结清的） | 0（`Completed`） | Committed 2，**Prepared 0** |
| 其余 17 份 | 均 1 | 多数 0 或 1，且都在途 |

**能过护栏的那份没有 `Prepared`，有 `Prepared` 的那些都过不了护栏。**最新的两份现场库最后一条
迁移分别是 `20260829083252` 与 `20260903110052`，都早于 0.2.0。

### 这说明的是顺序，不是缺陷

这个 runner 恢复的是**一次已授权现场运行留下的库**。要在 0.3.0 身份下跑它，就需要一份
0.2.0 之后的现场运行、且当时留下了一个在途的 Prepared 操作——而那种库只能由一次真车运行产生。
**所以它天然排在真车调试之后，不是之前。**

不要为了让它变绿去绕开护栏：护栏挡住的正是「在途 journey 跨过 messageId 语义变更」这件事，
而这个 runner 的全部断言都在重放 messageId。绕过它，跑出来的绿是假的。

## 本次不是什么

三次运行的 `classification` 均据实记为 `formalSlicePass: false`、`fullG3: INCONCLUSIVE`、
`releaseCandidate: INCONCLUSIVE`，四个正式切片（`W2G-IS-00` / `04` / `05` / `06`）保持
`INCONCLUSIVE`。staged G3 绑的是 commit、从 exact clone 重新 publish，**不碰任何候选产物**，
也不构成切片通过——一个切片通过要四道门全绿。

三个 runner 全程 loopback、无人值守，`journeyRuntimeEnabled` 与 `riotCreateDispatchEnabled`
都是 false，两个外部适配器指向死端口（`http://127.0.0.1:1`），
`realRiotOrderCreated`／`movementCommandSent`／`stationOperationRowFabricated` 均为 false。
没有真车参与，也没有任何外部副作用。
