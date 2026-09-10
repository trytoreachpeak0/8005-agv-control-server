# `G3`（补报）：四片 **全部 `PASS`**（纠正后的那一份）

`STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT`，30 条断言全绿，
`FP-IS-00` / `FP-IS-06` / `FP-IS-14` / `FP-IS-15` 的 `formalSlicePass` 都是 `true`。

## 这一轮新证的两件

**一、`PENDING_RESULT_REPLAY`。**激活命令在途被丢了一次（一次性 `drop-and-close`），实读：

| | |
| --- | --- |
| `commandsSent` | `2` |
| `commandMessageIds` | 一个 |
| `commandPayloadSha256` | 一个 |
| `commandConnectionIds` | 跨两个连接 |
| `resultsReported` | `1`，激活 `ACTIVATED`，生效配置 1 行 |

第一次投递连着连接一起死掉，重连之后补发的是**逐字节相同的同一行**，车只回答了一次。
REQ-0264 要求消息 7／8 走 `RELIABLE` 而不是 `REQUEST/RESPONSE`，就是为了这个：一台从没收到过命令
的车，必须让服务端什么都不相信；而重连要重新投递同一行，不是重新决定一次。

**二、「后一份整体取代前一份」。**这一轮第一次有两份告警快照到达（代 1 与代 3），投影是一行、停在
代 3 序号 2。在这之前每次 run 只到达过一份，这半条 REQ-0269 根本无从判起。

## 它纠正的是哪一份

**`../20260910-fp-is-14-pending-result-replay/` 的 `FP-IS-15` 是 `FAIL`，原样保留、未改一字。**
（那一份的 `FP-IS-14` 是 `PASS`——补报那五条当时就全绿了。）

红在断言不在产品。原断言写作「告警快照只出现在一个连接上」，悄悄假定整个 run 只有一次完整握手；
新增的 `drop-and-close` 造出第三个连接，那一次 journal 已经干净、于是走了完整握手。

本轮实读，三个连接里两个是完整握手：

```
clientConnectionIds        [1, 2, 3]
fullHandshakeConnectionIds [1, 3]      ← 带 CapabilitySnapshot 的
snapshotConnectionIds      [1, 3]      ← 带 OnboardAlarmSnapshot 的
```

连接 2 是恢复重连，一份快照都没重发。**数连接数从来不是要证的那件事**，要证的是这个 iff：带告警
快照的连接集合，就是带 `CapabilitySnapshot` 的连接集合，不多不少。断言改成了直接说这件事。

## 未在本轮证明的

- **告警内容仍然是空的**（`projectionAlarmCount: 0`）。查过：`OnboardAlarmBoard.Raise` 在整个车载端
  产品代码里**没有调用者**，只有测试在用。#28 把告警板、快照与传输都做了，**没有接上任何告警来源**
  ——与激活入口曾经的处境同一类。带非空内容的投影因此在任何 `G3` 里都证不了，缺的不是断言，是产品
  里还没有决定什么条件算一条告警。
- **激活的指纹不匹配路径没有证。**两次运行车都接受了指纹。那条路归 `run-staged-g3-restart.ps1`：
  重启车载端之前改它的 `slotIoMapping`，重启后再激活。
- `protocol-v1.0.0` 这个 tag **尚未打**。本次绑的是 commit。
