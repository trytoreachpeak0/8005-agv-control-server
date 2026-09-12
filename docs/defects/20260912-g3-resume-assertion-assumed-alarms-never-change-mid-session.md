# 缺陷：G3 续传断言假定告警在会话中途不会变化（R-5）

Status: fixed
Owner repository: `8005-agv-control-server`（`scripts/run-staged-g3.ps1`）
Found by: [`evidence/g3/20260912-fp-is-14-15-staged-6369616`](../../evidence/g3/20260912-fp-is-14-15-staged-6369616/)（`FP-IS-00`／`06`／`14` 为 `PASS`，`FP-IS-15` 为 `FAIL`）
Runner at discovery: `17cf478323f9c97e4aaea3f7c7a3cef732908249`
Product at discovery: 服务端 `63696161d036a4907a39f8597fd64cc6e4c755fd`；车载端 `w2g/b3-on-v2@f9efa301734128e850cb5460c20265232611ffff`
Peers: `slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`；`fp/v2-candidate@16e2567a7033883f00fc999f7fa08f954dd13a26`

编号接着 [`20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md`](20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md)
的 R-1～R-4。和那四条同一类：**产品行为是对的，红的是 runner 的断言。**这一条是 R-1、R-2 那条断言第三次需要修正。

## 现象

30 条断言 29 条 `PASS`，只有 `onboardAlarmSnapshotNotRepublishedOnRecoveryResume` 为 `FAIL_OR_INCONCLUSIVE`。实读
`onboard-alarm-snapshot-observation.json`：

```
clientConnectionIds        [1, 2, 3]
fullHandshakeConnectionIds [1, 3]
snapshotConnectionIds      [1, 2, 3]
appliedAcks                3
projectionAlarmCount       1
```

续传连接 2 上多了一份快照。当时的断言要求「带快照的连接集合恰好等于完整握手的连接集合」，所以判红。

## 为什么不是产品缺陷

**连接 2 上那份快照带的是新内容，不是把服务端已有的内容重报一遍。**三条独立的实读：

1. fault proxy 的时间线（`fault-proxy-events.ndjson`）：连接 1 在 11:14:52.607 发出握手快照，52.788 被注入故障断开；
   连接 2 在 54.809 续传，54.890 车载端上报 `SafetyStateChanged`，56.793 发出一份 `OnboardAlarmSnapshot`；
   连接 3 在 01.946 完整握手，又发一份。
2. 服务端写库（`logs/control.out.log`）：第二份快照入库是
   `UPDATE "OnboardAlarmSnapshots" SET "AlarmsJson" = @p0, ...`，**`AlarmsJson` 被改写了**；第三份是
   `UPDATE ... SET "CapturedAt" = @p0, ...`，没有 `AlarmsJson`，EF 判定内容与第二份相同。
3. 服务端库的最终行（stage 目录 `runtime/controlserver.db`）只有一条告警：
   `ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE`，`RaisedAt` 为 `2026-09-12T11:14:56.7924179+00:00`，恰好是连接 2 上
   那份快照发出的时刻。

staged 环境里出发安全信号是 `DISABLED`：车载端第一次读到的信号过期之后，这条告警第一次抬起，这一刻正好落在续传连接
上。按车载端 `a98679f` 的设计，**告警板内容与服务端最近 ack 的那份不同就报一份全量**，所以在连接 2 上报出去是对的。
如果改成等下一次完整握手才报，看板在续传期间会一直显示过时的告警集合，这正是 REQ-0269／0270 不允许的。

旧断言的注释写的是它要防什么：「续传时重发，会把**同样的告警板**以新的会话代交给投影，采纳规则会当它是新消息」。
那个缺陷的判据是内容相同，不是连接不同。旧断言在 `a98679f` 之前等价于这个判据，只因为那时告警板在会话中途从不变化，
真车报的快照永远是空的。

## 处置

两处修改，都在 `scripts/run-staged-g3.ps1`：

- **fault proxy 多记一个 `alarmsSha256`**，只覆盖 payload 里的 `alarms` 数组。`payloadSha256` 每次都不同，因为 payload
  自带序号和采集时间，靠它分不出两份快照是否说了同一件事。`alarms` 数组可以：告警条件持续期间，车载端告警板保留
  条目的 id 与首次抬起时间，同样的告警板序列化出同样的字节。
- **断言改成它真正要证明的两件事**，断言名不变（`g3-slice-evidence.ps1` 的认领表引用这个名字）：
  - 每一次完整握手都带一份自己的快照；
  - 其余每一份快照（在续传连接上，或在完整握手连接的会话中途）的 `alarmsSha256` 都与它前一份不同；
  - 另外要求 run 至少续传过一次，以及每份快照都记到了 `alarmsSha256`，否则第二条等于没有被检验。
- 观测文件新增 `snapshotsOnWire`：按线上顺序列出每份快照的连接、会话代、`alarmsSha256`、是否为完整握手自己的那份、
  是否与前一份内容相同。以后读证据时，不用再去翻服务端日志和数据库。

**新断言不会比旧断言宽松。**会话中途重报同样内容的缺陷，旧断言只在它发生在续传连接上时才抓得到；新断言在完整握手
连接的会话中途也抓得到。唯一放行的情况，是告警内容真的变了。

这条断言的时序本身不确定：告警可能在连接 1、2、3 的任何一段里第一次抬起，也可能在第一次握手之前就已存在。新断言
对这几种情况给出同一个判定，旧断言只有最后一种能过。

绿证据：同日修复后在干净的 harness 上重跑，见 `evidence/g3/` 下 `20260912` 开头、带修复 commit 的目录。
