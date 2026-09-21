# 最终 head 上的真机联调：16 轮 15 绿，`real-onboard-durable-ack-lost` 三连的第 3 轮红

run [35554054251](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35554054251)，`-f rig=real`。
三端（从每一轮证据的 `identity` 读的，不是从派发参数推的）：服务端 `fc144d0a565329a4115c657e73edc6d9fb5246b7`、
车载端 `e6bea2674a54a80ea92f9df8031999d46c77d321`、模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`。

## 红的是哪一条

`L2-DA-08`：期望 `2 connections, 1 open`，实际 `3 connections, 1 open (#1: relay dropped DurableAck for OperationResult; #2: onboard closed; #3: open)`。
这个场景故意丢掉**一次**持久确认，判的是「只断一次、恢复之后只剩一个连接」。第 3 轮多断了一次。

## 多断的那一次是什么（对照 `onboard-app-run-02.log` 与 `onboard-app-run-03.log`）

第一次断连两轮一样，是场景故意造的。**第 3 轮多出来的是第二次仓位操作（卸货）之后**：

```
10:38:55  OperationResult暂未收到DurableAck ... | TimeoutException: The operation has timed out.
10:38:57  迟到的DurableAck已取得（重连补发或重发）
10:38:57  上层会话不可用：The operation has timed out.。将在2秒后重连。
10:39:00  上层会话已建立：generation=3
```

也就是服务端对这一次结果的持久确认**超过了车载端的等待时限**，1.9 秒后才到。同一段里车载端的 IO 变化也明显变慢
（解锁到锁舌复位：第 2 轮 1.7 秒，第 3 轮约 4 秒），服务端期间重发了三次卸货命令。第 2 轮同一处一切正常。

## 已经排除的：本票没有改确认路径

处理仓位结果、回持久确认的是 `WireToGateStore.RecordOperationResultAsync` / `ApplyOperationResultAsync`
（第 2235–2500 行）；本票在这个文件里的改动行段是 13–14、30–36、43–106、563–896，**不相交**。入站处理
`OnboardMessageProcessor` 本票没有改。

## 没有排除的，以及最强的一条旁证

**本票让在途车也进派车轮次，且全车在途时不再提前结束轮次**，每一轮的工作量因此变大。它不在确认路径上，
但会和确认路径抢同一个数据库与 CPU。这条间接路径本票排除不了。

**旁证是同一时刻 vm01 上的负载**：

| 时刻（UTC） | vm01 上 |
| --- | --- |
| 02:35–02:37:41 | 本场景第 1、2 轮，绿 |
| **02:37:03** | 另一张票（`b7-262`）的全量 `test` 与合成 `l2` 同时开跑（run 35554768010／35554767923），构建与启动是负载峰值 |
| 02:37:42–02:39:05 | 本场景第 3 轮，02:38:45–57 那次确认超时在其中 |

服务端在超时窗口里的日志也是整机变慢的样子：一次对替身的请求 32 ms 收到响应头、452 ms 才处理完；车辆安全查询 880 ms；
有几秒只写了 3～7 行日志，前后都是几十上百行。

这是强相关，不是因果证明。三轮里两绿一红、红的那轮恰好压在另一张票两个重作业的启动峰值上。
