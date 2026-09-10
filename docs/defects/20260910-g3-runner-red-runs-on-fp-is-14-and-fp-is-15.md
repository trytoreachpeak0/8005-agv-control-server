# 缺陷：`FP-IS-14`／`FP-IS-15` 的 G3 里红在 runner 自己的四次运行

Status: fixed（R-1～R-3）；R-4 为操作规程缓解，runner 内未修
Owner repository: `8005-agv-control-server`（`scripts/run-staged-g3.ps1`、`scripts/run-staged-g3-restart.ps1`、`scripts/g3-slice-evidence.ps1`）
Found by: 下文每一节各自的 `Found by`
Product at discovery: 各节分列
Peers: `w2g/b3-on-v2@afba86e07116192bde386937c559cc7a70a00a7f`；`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`；`fp/v2-candidate@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`

2026-09-10 这两个切片的 G3 一共出过六份不绿的证据。两份红在产品（四处缺陷），写在
[`20260910-fingerprint-mismatch-locked-the-vehicle-out-of-its-own-repair.md`](20260910-fingerprint-mismatch-locked-the-vehicle-out-of-its-own-repair.md)。
**这里是另外四份：产品行为是对的，红的是 runner 的断言、装置或运行环境。**

单独成文，是因为「红在断言不在产品」这句话本身需要证据——每一节都贴了当次 transcript 或实读，说明为什么
那次红不是产品缺陷。四份红证据原样保留，纠正写在新目录里。

---

## R-1：告警快照断言假定每个连接都发一份

Found by: [`evidence/g3/20260910-fp-is-15-v2-alarm-snapshot`](../../evidence/g3/20260910-fp-is-15-v2-alarm-snapshot/)
Runner at discovery: `d88a8510a0f19818b7a34bccfa78c583e0697664`
纠正：`cda644a`；绿证据 [`20260910-fp-is-15-v2-alarm-snapshot-corrected`](../../evidence/g3/20260910-fp-is-15-v2-alarm-snapshot-corrected/)

### 现象

`FP-IS-15` `FAIL`，四条断言三条 `FAIL_OR_INCONCLUSIVE`，同一个原因。transcript（client-to-server）：

```
连接 1：SessionHello, CapabilitySnapshot, SafetyStateSnapshot, OnboardAlarmSnapshot, RecoveryStateReport
连接 2：SessionHello, RecoveryStateReport, SafetyStateChanged
```

### 为什么不是产品缺陷

这正是 `WireToGateSessionClient.HandshakeAsync` 的设计：journal 里有未确认的 outgoing 时走
`resumingInterruptedRecovery`，只重放未确认的那条，不重发任何快照——那三份已经在连接 1 上被服务端接受
过。这个 runner 的 replay 场景恰好掐掉 `RecoveryStateReport` 的 `DurableAck` 再关连接，重连必然走恢复
分支。断言写的是「每次连接都发」，实际是「每次**完整**握手发」。

### 处置

断言改成 runner 真能证的五条，其中 `onboardAlarmSnapshotNotRepublishedOnRecoveryResume` 把被误解的那个行为
钉住：恢复重连如果真的重发，投影会收到一份内容相同、会话代却是新的快照，那才是缺陷。「车重启后序号回到 1
仍被采纳」在这个 runner 里证不到，claim 表里写明归 `run-staged-g3-restart.ps1`。

---

## R-2：告警快照断言假定整个 run 只有一次完整握手

Found by: [`evidence/g3/20260910-fp-is-14-pending-result-replay`](../../evidence/g3/20260910-fp-is-14-pending-result-replay/)（该目录 `FP-IS-14` 为 `PASS`，`FP-IS-15` 为 `FAIL`）
Runner at discovery: `1d818536d54bd64f33cef6d3f43e11e4cddd3d9b`
纠正：`5ec2657`；绿证据 [`20260910-fp-is-14-pending-result-replay-corrected`](../../evidence/g3/20260910-fp-is-14-pending-result-replay-corrected/)

### 现象

`onboardAlarmSnapshotNotRepublishedOnRecoveryResume` `FAIL_OR_INCONCLUSIVE`。这一轮新加了一次性
`drop-and-close`（把激活命令在途丢一次），transcript 出现三个连接：

```
1  SessionHello, CapabilitySnapshot, SafetyStateSnapshot, OnboardAlarmSnapshot, RecoveryStateReport
2  SessionHello, RecoveryStateReport, SafetyStateChanged
3  SessionHello, CapabilitySnapshot, SafetyStateSnapshot, OnboardAlarmSnapshot, RecoveryStateReport,
   SlotConfigurationActivationResult
```

### 为什么不是产品缺陷

连接 2 是恢复重连，一份快照没重发；连接 1 与 3 是完整握手，各发一份。**产品行为完全正确。**R-1 纠正后的
那条断言写作「告警快照只出现在一个连接上」，悄悄假定了整个 run 只有一次完整握手；新的 fault 造出第三个
连接，而那一次 journal 已经干净，于是走了完整握手。

### 处置

断言改成它真正要证的那个 iff：**带告警快照的连接集合，就是带 `CapabilitySnapshot` 的连接集合**，不多不少。
顺带补上 `onboardAlarmProjectionKeptOnlyTheLatestOfSeveralSnapshots`——这一轮第一次有两份快照跨代到达，
投影一行停在代 3 序号 2，「后一份整体取代前一份」其实已经发生，只是没有断言在看。

**这是同一类错误的第二次。**R-1 修的是「每个连接」，R-2 修的是「只有一个完整握手」，两次都是在数连接，
而要证的从来不是连接数。

---

## R-3：拒绝路径的篡改点选成了 `appsettings.json`

Found by: [`evidence/g3/20260910-fp-is-14-fingerprint-mismatch`](../../evidence/g3/20260910-fp-is-14-fingerprint-mismatch/)
Runner at discovery: `3ca39447d7a64a5c757d7117f733b8e854931a06`（被测服务端 `b6064690`）
纠正：`1ab0bb9`

### 现象

三条拒绝路径断言全红，但红的原因是**两次激活都被接受了**：

```
whileMatching  → ACTIVATED, version 2, de93ca3d…
afterTamper    → ACTIVATED, version 3, de93ca3d…   ← 指纹一个字都没变
```

### 为什么不是产品缺陷

车上的生效配置是一份持久化文档（publish 目录下的 `active-slot-configuration.json`），`appsettings` 的
`ioModule.slots` 只在这份文档还不存在时被 `OnboardActiveSlotConfigurationFactory` 用来生成一次初始值。
第一次激活写过之后，重启的车从文档读，再不看 `appsettings`。**这个行为是对的**：车装着哪一版只有生效配置
存储说了算，不该被一次配置文件编辑悄悄改掉。

### 处置

篡改点改为那份文档，只改 `Slots`、不改文档里同时存着的 `Fingerprint`——`ActiveSlotConfiguration.Fingerprint`
是从 `Slots` 算出来的计算属性，读回来不会采纳文档里的值，这正是真实篡改的样子，也顺带查一次车会不会轻信
一个与内容不符的摘要。加一道 guard：篡改前后文档哈希必须不同，否则直接 throw——一次什么都没改的「篡改」
会让后面的拒绝断言变成对着原配置断言，那种绿是假的。

改对篡改点之后的下一次运行暴露的是真正的产品缺陷，见产品缺陷文件的 D-1、D-2。

---

## R-4：`INCONCLUSIVE_RUNNER_ERROR`——机器内存不够

Found by: [`evidence/g3/20260910-fp-is-14-15-activation-and-alarm`](../../evidence/g3/20260910-fp-is-14-15-activation-and-alarm/)
Runner at discovery: `0af174312e61f2507a29742684c660cdf43a7b72`
绿证据：同命令重跑 [`20260910-fp-is-14-15-activation-and-alarm-retry`](../../evidence/g3/20260910-fp-is-14-15-activation-and-alarm-retry/)

### 现象

不是断言红，是跑不出结论：

```
field-ops-seed-approved-facts exited with code -532462766
logs/field-ops-seed-approved-facts.log:  Out of memory.
```

15.6 GB 的机器当时只剩 996 MB——publish 阶段留下的十几个 MSBuild node 各占 100 MB 上下。四片都写了
`gate-result.json`，状态不是 `PASS`。

### 处置

**只是缓解，没有修。**重跑前在 runner 之外执行 `dotnet build-server shutdown`，并设
`MSBUILDDISABLENODEREUSE=1`、`DOTNET_CLI_USE_MSBUILD_SERVER=0`，命令其余部分一字未改。同日之后的每一轮
G3 都照此执行，没有再出现。

**runner 自己不做这件事**，所以换一个人、换一台机器照 `-EvidenceRoot` 那一行命令直接跑，仍可能撞上。要真正
修掉，应当让 runner 在 publish 之后自行关掉构建服务器、或在 publish 的子进程环境里设那两个变量。这不在本文件
范围内，记在这里是为了不让「后来没再出现」被读成「已经修好」。
