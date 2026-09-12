# ControlServer.ProtocolFaultProxy

挡在车载端和 ControlServer 的车载协议监听之间，逐行转发 NDJSON，**只在被要求时吞掉某一类报文的
`DurableAck`、然后断开那条连接，或者不挑任何一行、直接断开**，别的一律原样转发。

```
车载端 ──TCP──► 代理 ──TCP──► ControlServer :58405
                  │
                  └── 服务端回的 DurableAck(acceptedMessageType = X) → 不转发，两头都断
```

## 为什么需要它

车载端的持久出站报文（`OperationResult`、`OperationProgress`、`SublotSubmitted`……）先写进本地
journal 再发，收到 `DurableAck` 才标成已确认。**服务端已经收下、ack 却没到车上**时，车在下一次连接
里原样补发：同一个 `messageId`、同一个 `sentAt`，只把 `sessionGeneration` 换成新会话的
（`RebindSessionGeneration`）。这是 ADR-cross-0030 要求的「重连补发同一消息时沿用原编号」。

L2 里两端走 loopback，这个窗口不会自己出现，**这条补发路径从来没有对着真服务端跑过**
（[`8005-agv-control-server#30`](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/30)）。
车载端 G2 里有同形的用例，但它的替身只比 payload，而真服务端比的是整行字节。这个代理就是那条丢了
ack 的链路。

## 为什么这不是「自己写的假象」

- **服务端真的收下并提交了那条报文，也真的写出了 ack**；代理只是不把它交给车。
- **车真的没收到 ack，真的看见连接断了**——从两端看，这就是「服务端提交之后链路掉了」。
- 之后发生的一切都是两端出厂的真代码：车载端的重连与 journal 补发、服务端对补发的判定。

**它不等价于什么，也要说清楚**：它不是一条不稳定的网络。别的每一行都逐字节转发，丢的恰好是场景要求的
那一条、次数恰好是要求的次数。

## 用法

```powershell
.\ControlServer.ProtocolFaultProxy.exe `
  --ProtocolFaultProxy:port=58415 `
  --ProtocolFaultProxy:listenPort=58416 `
  --ProtocolFaultProxy:target=127.0.0.1:58405
```

`--ProtocolFaultProxy:target` **没有默认值，而且只接受 loopback**：一个能吞 ack 的中继不能挡在任何
别人依赖的服务器前面。车载端的 `wireToGate.port` 改指 `listenPort`。

| 项 | 值 |
| --- | --- |
| 数据面 | 纯 TCP，`listenPort`（L2 用 58416），监听地址与控制面相同 |
| 控制面 | `/control/v1/health`、`/snapshot`、`/drop-durable-ack`、`/disconnect`、`/reset`、`/openapi.json` |
| 机器契约 | `openapi.json`，运行时在 `/control/v1/openapi.json` |
| 监听 | 仅 loopback，默认控制面 58091、数据面 58092 |

布一个计划：

```powershell
PUT /control/v1/drop-durable-ack  { "runId": ..., "commandId": ..., "acceptedMessageType": "OperationResult", "count": 1 }
```

`count` 为 0 撤掉计划，上限 10。每次布置都是一个新计划、各自计数，所以丢过一次之后可以再布一次。
`/reset` 撤计划，不清流量记录——已经过去的流量是证据。

断开一次：

```powershell
POST /control/v1/disconnect  { "runId": ..., "commandId": ... }
```

把此刻开着的每条连接两头都关掉，**不挑任何一行去丢**，车自己重连；应答里列出关掉的连接号，快照里那几条连接的
`closedBy` 记成 `relay disconnected on request`。同一个 `commandId` 重试只回上次关掉的那几条、不再断开，
免得应答丢了换来第二次重连。它不是计划，不动 revision。

**它不保证断开那一刻在路上的行送到。**已经读进缓冲、还没转出去的行，和真断线一样随连接一起没了；流量记录又是
在转发之前写的，所以快照里断开前的最后几行不保证到了对端。判据别拿断开那条连接的尾巴当「对端收到了」。

**为什么要一个不丢 ack 的断开**：丢 ack 会让车补发，之后发生的事就混进了车的补发路径（#30、#33）。
[`8005-agv-control-server#31`](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/31) 要看的是
**服务端**把自己没被确认的报文重放进新会话、车怎么处理，那需要一次干净的断线。

## 三条边界

1. **逐字节转发。**两端都用单个 LF 分行、都对整行字节算哈希，所以代理读一行不带 LF、写回时补一个 LF。
   两端都不发 CR。
2. **丢的是 ack 所在那一行，以及同一连接上之后的一切。**服务端把 ack 和紧跟的 `SessionReadiness`
   一次写出；一条丢了 ack 的链路不会只丢半截。
3. **不是 JSON 的行照样转发。**判它合不合法是接收端自己解析器的事，不是这个替身的。

## 快照里有什么

`traffic.connections` 记每条连接的开、关和谁关的；`traffic.lines` 记每一行的连接号、方向、
`messageType`、`messageId`、`correlationId`、`sessionGeneration`，`DurableAck` 另记
`acceptedMessageType`；`traffic.drops` 记每次丢弃。**只记信封身份，不记 payload**——`SessionHello`
里有车载端凭据。

有了连接号，「车重连了几次」「补发发生在第几条连接、换没换世代」「补发有没有被确认」都能直接读出来，
不用去翻两端的日志。
