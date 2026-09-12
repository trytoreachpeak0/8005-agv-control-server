# `G3`（主 runner）：已发布的 `protocol-v1.0.0` 上 `FP-IS-00`／`06`／`14`／`15` **全部 `PASS`**

`STAGED_G3_RECOVERY_REPLAY_PASS`，`assuranceLevel` 为 `STAGED_REBUILD`，30 条断言全绿，四片均为 `formalSlicePass: true`。本轮没有带 `-Slice`。

**它取代 `../20260912-fp-is-14-15-staged-harness-58f1a49/`，作为主 runner 这四片的现行证据。**那一份绑的是协议候选 `16e2567`，原样保留。

## 为什么要重跑

2026-09-12，产品负责人决定发布批准也可以由他授权的 AI agent 给出，协议治理随之修改。协议候选变为 `9f22db8`（manifest `a0e1deed…`），
同日发布为 `protocol-v1.0.0`，批准由 AI 给出、授权人为产品负责人。两端随后改为绑定这个已发布身份：服务端 `6b21662`，车载端 `98f4e06`。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `6b21662c60a2e13aaf86043b146d3d886cf91dd1` |
| `onboard` | `c86bac5eaec57c36351f7d45b458deaa42fde22e`（`origin/w2g/b3-on-v2` 的顶，即 `98f4e06` 加上 G2 证据） |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| `harness` | `a1243a8f5870bcacfd3257ddf2422bebabf8e2db`，`harnessWorktreeCleanAtStart: true` |

`run-result.json` 的 `protocol` 节：

- `tag`：`protocol-v1.0.0`；**`tagExists`：`true`**，指向 `9f22db8`
- `approvalStatus`：**`APPROVED_RELEASE`**
- `manifestSha256`：`a0e1deed…`
- `g1`：`PASS`

`6b21662` 给 runner 新加了一条检查：身份声称已发布时 tag 必须存在。这是它第一次实际跑到。**这也是第一份绑定已批准协议发布的 G3 证据。**

## 实读

**`FP-IS-15`**：`snapshotsOnWire` 如下。

| 连接 | 会话代 | 类型 | `alarmsSha256` | 与前一份相同 |
| --- | --- | --- | --- | --- |
| 1 | 1 | 完整握手自己的那份 | `dd096651…` | — |
| 2 | 2 | 续传连接，会话中途 | `d766dc61…` | 否 |
| 3 | 3 | 完整握手自己的那份 | `d766dc61…` | 是（握手总是报全量，允许相同） |

- `appliedAcks` 为 3，等于发送数。
- 投影每车一行：会话代 3，序号 3，告警 1 条。
- 时序形态与 `16e2567` 那一轮相同：续传期间抬起一条告警，staged 环境里就是出发安全信号不可用；车载端在续传连接上报出新的全量，下一次完整握手再报一份相同内容。

**`FP-IS-14`**：激活命令发出 2 次，分别在连接 2 和 3 上，`messageId` 只有一个，在途被丢之后逐字节重放。车报 1 次结果，激活行 `ACTIVATED`，version 2。
生效配置行的指纹是 `de93ca3d…`，与激活行一致，说明两端各自算出了同一个指纹。

**`FP-IS-00`／`FP-IS-06`**：恢复重放、可靠重试的断言与上一轮逐项相同，全绿。

## 未在本轮证明的

- 整体 G3 仍是 `INCONCLUSIVE`：`FP-IS-01`／`02`／`03`／`07` 没有 G3 面。
- 真进程之间仍然只出现过两种告警：本 runner 里的出发安全信号不可用，以及进程重启 runner 里的仓位配置指纹不符。
- 车重启后的告警采纳、指纹不符的拒绝路径，归进程重启 runner 证明，见同日 `20260912-protocol-v1.0.0-restart-harness-a1243a8/`。
