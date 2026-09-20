# CI 真装置 run `35513390399`（control-server#204）

`l2.yml` 的 `real-rig` 作业，手动 `-f rig=real`，在 vm01 的交互式 runner 上跑。七次运行，**六次 PASS、
一次 FAIL**。`_stage` 目录（7 MB 的构建中间产物）未入库，其余原样。

## 四行核对

| | |
| --- | --- |
| 服务端 | `de29ec1649596012e39bb71de45f22ba4ff10605`（本票 PR head） |
| 车载端 | `24af41e4ab769f10cd15381fd3b4a56babb62c78`（onboard-hmi `w2g/fp-v2-impl`） |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（slots-simulator `main`） |
| **这一遍真跑起来了** | 七个场景目录里**没有** `RIG_COMMIT_GUARD`／`RIG_DESKTOP_LOCK`／`RIG_DEADLINE`；判据条数 14／14／14、10／10／10、7 —— 场景走到了底 |

三端提交取自**每个场景自己写的身份表**（各目录的 `SUMMARY.md`），不是 workflow 写的 `commits.json`：
checkout 那一段显示的是集成分支顶端，看错方向会去怀疑一份有效证据。七次的三端组合完全一致。

## 结果

| 场景 | 结论 | 判据 |
| --- | --- | --- |
| `real-onboard-expected-action-overdue-01/02/03` | PASS ×3 | 14/14，含本票新增的 `L2-EAO-14` |
| `real-onboard-durable-ack-lost-01/02` | PASS ×2 | 10/10 |
| `real-onboard-durable-ack-lost-03` | **FAIL** | 7/10，见下 |
| `real-onboard-restart-while-waiting-operator-01` | PASS | 7/7，含收紧后的 `L2-RW-02` |

本票收紧或新增的判据**全部通过**，包括红的那一次里的 `L2-DA-09`（干净扫描 31 轮、失败 0 轮、检出 0 次）。

## `-03` 那次红：既有的间歇缺陷，不归本票

**同一棵树跑三遍，2 绿 1 红**，这是可得证据里最硬的一档对照——三次全在同一个 artifact 里。

红的三条：

```
FAIL L2-DA-03  #2 SessionHello → SessionAccepted → SlotOperationCommand → OperationResult
FAIL L2-DA-04  gen 3 / RecoveryRequired / DEPARTURE_SAFETY_NOT_READY
FAIL L2-DA-08  3 connections, 1 open (#1: relay dropped DurableAck; #2: onboard closed; #3: open)
```

`L2-DA-08` 把因果讲全了：**连接 #2 是「onboard closed」——车自己关的，不是服务端掐的**（`L2-DA-07`
因此仍 PASS）。车在握手里读到一条它没在等的报文，当场断开重连，于是多了第三条连接、世代变 3。

### 机理

两个时刻，中间就是那个窗口：

- **`OnboardPeer.Attach` 在处理完 `SessionHello` 之后**就发生（`OnboardTcpServer.cs:206-212`，条件只要求
  `SessionGeneration` 与 `AgvId` 都有了）。从这一刻起这条连接已经可以被路由，引擎的出站推送发得进来。
- **`HandshakeCompleted = true` 要等到收下 `RecoveryStateReport`**（`OnboardMessageProcessor.cs:403`）。
  注释：「The recovery report is the last thing the vehicle sends in its handshake; from its answer on, the
  vehicle reads the connection in its receive loop and may be asked for things.」

连接已经可被推送，而车载端还在一问一答的握手里、根本没进接收循环。

**这个危险服务端自己认得，只是那道门只装在了一条路径上。**`AppendSafetySnapshotRequest`
（`OnboardMessageProcessor.cs:212-222`）第一件事就是 `if (!state.HandshakeCompleted) { return response; }`，
注释写着「Never inside the handshake. …… so a request slipped in between would be read as the next answer and
break the handshake」——**一字不差就是这次发生的事**。而 `OnboardPeer.SendAsync` 那条主动下发的路径没有
这道门，`SlotOperationCommand` 就是从那里进来的。

### 与本票的关系

**不是本票引起的，而且时序上不可能是。** 本票在这条场景里只动了 `L2-DA-09` 那一块：

| | 行号 |
| --- | --- |
| `L2-DA-03` 判据 | 216 |
| `L2-DA-04` 判据 | 228 |
| 本票的第一处运行时代码（10 秒采样） | **239** |

顶层那两句（`New-L2HmiPhraseWatch`、`$unfinishedElements` 的 scriptblock）只建对象，不做 UIA、不碰连接。

### 追到哪里

挂在 onboard-hmi#50「握手途中服务端多写的行……都会多断一次」。那张票列了两个窗口，这次是**第三个**，
而且差别值得记：它的两个窗口都是「补发的应答后面多写几行」，这次是「`SessionAccepted` 之后、补发之前
插进来一条纯粹的主动下发」，位置更早、也不是任何应答的尾巴。

**那张票明写「以下全部按代码推演，没有实测」——这是这一族第一次有真装置实测证据。**
它要回答的第一个问题里就列着「还是由服务端保证握手完成之前不主动下发」，这次撞到的正是那个选项所指的缺口。
