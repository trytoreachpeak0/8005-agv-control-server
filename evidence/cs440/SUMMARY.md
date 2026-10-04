# cs#440 / cs#448：负载下偶发红的确定性复现与修复

基线：`fp/v2-impl@88086c4b`。只改测试，不改产品代码。

## 做法

每一种偶发红都先找到机理，再用一个**确定性注入**把它变成必红：

- 修复前的测试加上注入，每组跑 2 遍，结果必须全红，而且红的位置和报错要与 CI 或本机实际出现过的那一次一致。
- 修复后的测试加上同一处注入，每组跑 2 遍，结果必须全绿。

注入补丁在 `probes/` 下，只在跑的时候临时打上，跑完即撤，不进提交。脚本 `probes/run-probe.ps1` 负责打补丁、构建 Release、按过滤条件跑用例、撤补丁，每次运行的记录在 `runs/<标签>/`。修复后的注入复跑另外留了 TRX，里面有用例自己打印的轨迹。

`before-P1`、`before-P2` 两组的控制台日志在修正脚本编码之前录制，中文是乱码；判定依据是其中的英文断言原文与 `results.txt`。

## 结果总表

| 组 | 机理 | 注入 | 修复前 | 修复后 |
| --- | --- | --- | --- | --- |
| P1 | #440 原题：等的是旧代次离开路由表，断言的却是之后才写的 1003 | `Detach` 之后延迟 2 s | 2/2 红，`:205` 缺 1003 | 2/2 绿 |
| P2 | #440 评论一：每次重连只给 4 s 握手 | 旧代次离表后，服务端晚 4.5 s 才回答握手 | 2/2 红，「此后开始的第一次尝试没有成功…Failed TimeoutException」 | 2/2 绿 |
| P3 | #440 评论二：先超时的写是接收循环自己的 HeartbeatAck，推送只看到「连接已关」 | 推送的写超时晚 3 s 到点 | 2/2 红，`Not found: "did not finish within 5 s"`（1 s 那一格同理） | 2/2 绿 |
| P4 | 扫描发现：答完握手到 Attach 之间只等 1 s，慢的 Attach 被误判为拒绝 | 旧代次离表后，Attach 晚 1.5 s | 2/2 红，同 P2 的判词，之后每次都是 AnsweredButNotRoutable | 2/2 绿 |
| P5 | #448：心跳与读端点之间超过 6 s 存活窗口，端点如实判车失联 | 心跳之后、第一次 GET 之前停 6.5 s | 2/2 红，`Assert.Single() Failure: The collection was empty` | 2/2 绿 |
| P6 | 扫描发现（调度并入本票）：静默窗口 250 ms，对照那一半的客户端写得晚，连接先被静默关闭 | 客户端连上后等 400 ms 再写 `SessionHello` | 2/2 红，`IOException: An established connection was aborted` | 2/2 绿 |

不注入的基线：修复前两个类共 5 条，修复后 `OnboardPowerLossReconnectTests` 与 `ExpectedActionOverdueTests` 全类 26 条、`OnboardSilentLivenessLossTests` 全类 10 条，都通过。

## 各组要点

**P1。**`OnboardTcpServer.HandleClientAsync` 的 `finally` 先 `Detach`（旧代次离表），异常随后穿过作用域、连接、流的释放，最后才在 `ServeAsync` 的 `catch` 里记 1003。测试改为先等 1003 出现（上限是挂死保护 30 s），再读日志。

**P2。**4 s 的 `AttemptBudget` 是这个测试类自己定的，产品里没有对应的预算：服务端不给握手设期限，合成车载端也不设。现场车载端设的是每一条答复 2.5 s（`8005-agv-onboard-hmi` 的 `WireToGateSessionClient.ReadEnvelopeAsync`，`messageTimeoutMs=2500`），超时就隔 2 s 重连，那管的是现场恢复得快不快。本类要证的是「旧连接一放掉，下一次就进得来」，所以每次尝试改为按结果判，只受剩余的挂死保护约束。

**P3。**两次写排在同一道发送门后、各自计时；推送把缓冲区写满的那一刻如果正轮到 HeartbeatAck，卡住和先到点的就是它，产品的 `OnboardPeerConnection` 注释承诺过这种情况。修复后的轨迹（`runs/after-P3/*.trx`）：推送只记下 `... is closed or broken ...`，1003 的异常是 `A write to the Onboard peer 'AGV-POWER-01' (session generation 1) did not finish within 5 s`。测试改为在推送的失败和 1003 的异常两处找，并要求同一条失败同时带配置的秒数、确切的类型和车号。原来三处 `Contains` 可以分别由不同的失败满足，现在更严。

**P4。**修复后的轨迹（`runs/after-P4/*.trx`）：旧代次在 t=5.5s 离表；前三次「答了但不可路由」都在离表之前，是真的被拒；离表后的第 4 次虽然 Attach 晚了 1.5 s，仍判为 `Routable`。

**P5。**端点只按 `SessionLiveness.Timeout`（6 s）过滤车辆，安全快照的 `observedAt` 不参与过滤，票面「快照不新鲜」那条推测不成立。修复后只在端点答「这台车失联」这一种情况下补发心跳再读，上限 10 次；输出「读了 2 次端点，前 1 次答的是这台车失联；最后一次答的是它在线」。

**P6。**扫描时发现，CI 上没有观测到过。服务端静默关闭时那一行还没被读走，Windows 回的是重置而不是 EOF，所以修复前的红是 `IOException`。修复后只在「这条连接以 1005 收场」时换新连接重来，上限 10 次；1005 在关连接之前记下，客户端看到连接没了时它已在日志里。输出「对照那一半用了 2 条连接」。
