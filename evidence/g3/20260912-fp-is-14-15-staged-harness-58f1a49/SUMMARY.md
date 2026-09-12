# `G3`（主 runner）：`FP-IS-00`／`06`／`14`／`15` **全部 `PASS`**（服务端 `6369616`，协议 `16e2567`）

`STAGED_G3_RECOVERY_REPLAY_PASS`，`assuranceLevel` 为 `STAGED_REBUILD`，30 条断言全绿。本轮**没有带 `-Slice`**，四片各写一份 `gate-result.json`，
并且都是 `formalSlicePass: true`。

**它取代下面这些证据，作为主 runner 的现行证据：**

- `FP-IS-00`、`FP-IS-06`、`FP-IS-14`：`../20260910-fp-is-14-15-staged-6dc4bc8/`
- `FP-IS-15`：`../20260910-fp-is-15-staged-eefb3a8/`

它同时**纠正了同日首跑那一份** `../20260912-fp-is-14-15-staged-6369616/`，见下文。以上几份都原样保留、未改一字。

## 为什么要重跑

产品负责人 2026-09-12 批准把单人签名规则移植到协议候选，并重跑全部门禁。协议候选因此换成了 `16e2567`，content manifest 从 `84f984ea…`
变为 `25fd6689…`。两个 runner 认领的四片证据绑的都是旧 manifest，所以四片一起重跑，而不只是批次 3 的两片。
同一轮里两端还各有一处产品改动：

- 服务端 `5f7a34e`：看板集中显示车辆的全部告警（REQ-0270）。
- 车载端 `a98679f`：告警接上真实来源；告警板内容与服务端最近 ack 的那份不同，就在会话中途报一份全量。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `63696161d036a4907a39f8597fd64cc6e4c755fd` |
| `onboard` | `f9efa301734128e850cb5460c20265232611ffff`，即 `origin/w2g/b3-on-v2` 的最新提交 |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `16e2567a7033883f00fc999f7fa08f954dd13a26`，manifest `25fd6689…`，`tagExists: false` |
| `harness` | `58f1a496bf5077aa405eb297703d8aa73c6d529c`，`harnessWorktreeCleanAtStart: true` |

两端的绑定与同日的 `CONTROL_SERVER_G2`（服务端 `6369616`）和 `ONBOARD_HMI_G2`（车载端 `ad0e507`，就在 `f9efa30` 之下）一致。

## 实读

**`FP-IS-15`：这是第一次由真车载端报出非空告警。**看板投影最终为 1 条告警，
即 `ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE`。staged 环境里出发安全信号是 `DISABLED`，第一次读数过期后，这条告警就会抬起。
`snapshotsOnWire` 如下：

| 连接 | 会话代 | 类型 | `alarmsSha256` | 与前一份相同 |
| --- | --- | --- | --- | --- |
| 1 | 1 | 完整握手自己的那份 | `51461be2…` | — |
| 2 | 2 | **续传连接，会话中途** | `52bbc446…` | 否 |
| 3 | 3 | 完整握手自己的那份 | `52bbc446…` | 是（握手总是报全量，允许相同） |

告警在续传连接 2 存续期间抬起，车载端就在连接 2 上报了一份新的全量快照。完整握手的连接 3 又报了一份，内容与连接 2 相同。
`appliedAcks` 为 3，与发送数一致；投影每车一行，行的会话代是 3，序号是 3。

`FP-IS-14`：激活命令在途被丢一次，之后在连接 3 上逐字节重放，`messageId` 只有一个；车报一次结果，库里激活行为 `ACTIVATED`，
生效配置行的指纹与激活行一致，两端各自算出了同一个指纹。

`FP-IS-00`／`FP-IS-06`：恢复重放、可靠重试的断言与上一份逐项相同，全绿。

## 被纠正的那一份

`../20260912-fp-is-14-15-staged-6369616/`（harness `17cf478`）中，`FP-IS-15` 为 **`FAIL`**，其余三片 `PASS`。
红在 `onboardAlarmSnapshotNotRepublishedOnRecoveryResume`：那一份的连接和告警时序与本目录相同，但当时的断言要求
「带快照的连接集合恰好等于完整握手的连接集合」，于是把续传连接 2 上那份快照判成了重复上报。

实读证明那份快照带的是新内容：服务端入库时改写了 `AlarmsJson`，告警的 `RaisedAt` 正是那份快照发出的时刻。
**产品行为是对的，红的是断言。**修复在 `58f1a49`：fault proxy 多记一个只覆盖 `alarms` 数组的 `alarmsSha256`，
断言改为比内容而不比连接。缺陷单是 `docs/defects/20260912-g3-resume-assertion-assumed-alarms-never-change-mid-session.md`（R-5）。
新断言在提交前用九组构造的 transcript 离线核过。本目录是修复后第一次实跑，时序与首跑相同，所以正好验证了修复。

## 未在本轮证明的

- 整体 G3 仍是 `INCONCLUSIVE`：`FP-IS-01`／`02`／`03`／`07` 本批次没有可测的面。
- **告警条件只证到了一条。**staged 环境能自然触发的只有出发安全信号不可用；IO 离线、锁反馈丢失、行驶中仓门未锁等条件，
  只在车载端 G2 的求值测试里证过，没有在真进程之间出现过。
- 车重启后告警快照的采纳，由进程重启 runner 证明，见同日 `restart-harness-58f1a49` 那一份。
- `protocol-v1.0.0` 这个 tag **尚未打**，没有任何人签过 attestation。
