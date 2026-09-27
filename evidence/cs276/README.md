# cs#276 证据：车载机断电后重连

票：trytoreachpeak0/8005-agv-control-server#276。全部在本机回环上做，没有碰 agv01、factory01 与 RIoT。

每条都标了「读到的」（跑出来或读代码读到的）还是「推的」。

## 第一步：定因（step1/）

探针源码以 `.cs.txt` 存档（不参与编译），原始输出与之同目录。

**装置**：真实的 `OnboardTcpServer`、真实的 `OnboardMessageProcessor`、内存 SQLite、合成车载端 `OnboardPeerSession`；两者之间一个字节中继。断电时中继不再往服务端转发任何东西，也永远不关服务端那一侧的 socket（没有 FIN，没有 RST）。服务端写来的字节，要么读走丢掉，要么完全不读、让发送缓冲区堆满。车载端随后重连，失败隔 2 秒再试。判据：新一代会话在 `OnboardPeer` 里可路由，且连续 3 秒不被服务端关掉。

| 线与提交 | 形状 | 结果（读到的） |
| --- | --- | --- |
| MVP `670bdd45` | M0 对照：旧连接正常关闭 | 第 1 次、0.0 秒建立 |
| MVP `670bdd45` | M1 旧连接半开 | 60 秒 10 次全部建不起来，每次 4 秒等不到应答 |
| v2 `2ded1b38` | S1 服务端写入被读走、不推送 | 9.3 秒可路由，3 次 |
| v2 `2ded1b38` | S2 同上，每 200 ms 往旧会话推送 | 9.1 秒，3 次 |
| v2 `2ded1b38` | S3 服务端写入不被读（发送缓冲区堆满），每 50 ms 推 64 KiB | 9.1 秒，3 次 |
| v2 `2ded1b38` | S4 失败后立即重试 | 9.2 秒，7 次 |
| v2 `2ded1b38` | S5 反事实：静默窗口改成 1 小时 | 40 秒 14 次全部失败 |

v2 上每次失败的服务端日志都是 `An Onboard peer is already attached for 'AGV-CS276-01'`；旧连接在断电后约 6 秒被 `went silent ... closing it (ADR-cross-0027)` 关掉，下一次重试就成功（读到的）。

`step1/v2-2ded1b38-*.txt` 末尾的 `Test Assembly Cleanup Failure` 是合成车载端心跳的 schema 违规，与定因无关，由本分支第一个提交修掉（见下面 H0）。

## 回归测试的反向验证

新测试类 `tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs`，两条：S1 形状与 S3 形状。每个变异都把产品代码临时改一处，`--no-incremental` 重建，跑本类加 `OnboardSilentLivenessLossTests`（共 12 条），再用备份还原。`*.record.md` 是变异的 diff 与退出码，同名 `.txt` 是测试输出。

| 变异 | 改了什么 | 红了哪几条（读到的） |
| --- | --- | --- |
| M1 | 生产构造函数传入的静默窗口改成 1 小时 | 新的 2 条；另有 `OnboardSilentLivenessLossTests` 的 2 条（它们钉的是构造函数取项目级常量） |
| M2 | `SessionLiveness.Timeout` 改成 60 秒 | 新的 2 条；另有钉 6 秒常量的 1 条 |
| M3 | 静默关闭时不把旧连接从路由表摘掉（`finally` 里加 `&& !liveness.Expired`） | **只有新的 2 条**：旧连接关了、窗口也对，但车仍然连不上，现有测试一条都看不到 |
| H0 | 把合成车载端心跳的 `observedAt` 放回去 | `Failed: 0` 但 `Errors: 1`、退出码 1：出站 schema 门禁报 4 条违规，全是 Heartbeat 的 `#/payload/observedAt [additionalProperties]`（范围是这 12 条用例，不是全量；提交 `f04d55fd` 的说明误写成 3 条，以这里为准） |

新 2 条红的都是同一句判据：「断电后 12 秒内新会话一直不可路由」，没有一条红在前提断言上（读到的）。M1、M2 下现有护栏也红，说明窗口的值本来就钉住了；新测试补上的是 M3 那种「值没变、行为坏了」的格子。

还原后重建，12 条全绿：`green-2ded1b38.txt`。

## cs#342 断线重连模型

| 文件 | 组合数 | 结果（读到的） |
| --- | --- | --- |
| `cs342-model-300.*` | 300，masterSeed=342 | 无 violation 行；录入请求 300/300 到车；ack 冲突 0；`ORDER_HANG` 5 轮。与 `evidence/cs362/green/03-cs342-reconnect-model.txt`（09-26）逐项相同 |
| `cs342-model-1000.*` | 1000，masterSeed=342 | 无 violation 行；录入请求 1000/1000 到车；ack 冲突 0；`ORDER_HANG` 13 轮（这是带等人码的失败轮计数，不是违规） |

已知缺陷表 `ReconnectModel.KnownDefects` 目前为空。

**模型对本票说明不了什么**（读到的）：模型不经过 `OnboardTcpServer`（它按那里的顺序自己驱动处理器），也不用合成车载端 `OnboardPeerSession`，而本票的两处改动恰好就是这两样。模型全绿只说明本分支没有碰到它覆盖的那些路径，本票的判断靠上面的回归测试与反向验证。
