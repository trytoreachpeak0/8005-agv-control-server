# 缺陷：跨会话代次重放快照造成自我维持的重连死循环

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: [`到站推进首次可达后的联合运行`](../../evidence/g3/20260829-placeholder-fix-confirms-leg/SUMMARY.md) 之后的到站验证运行
Product at discovery: `ControlServer_MVP@bf22a48d5d0e386e7af21d162800f7cf09b0032d`
Fixed in: `ControlServer_MVP@b426ef6359b38bbd73b70452359003767ed30b04`
Verified by: [`到站之后首次走通：三处服务端修复的现场验证`](../../evidence/g3/20260829-arrival-to-sublot-field-verify/SUMMARY.md)（现场端到端效果）、[`staged G3 重绑当前双端 3d8b00c + 304e6ad`](../../evidence/g3/20260829-staged-g3-rebind-3d8b00c-304e6ad/SUMMARY.md)（跨代次重放路径本身）
Peers: `OnboardHmi_MVP@84b7f3f`、`slots-simulator@fb5f7c5`、`protocol-v0.1.1@1531489`

## 现象

`"--"` 占位符与 `orderState 5` 两处修复之后，旅程首次真正走到取货到站并调用
`PublishPickupStateAsync` 发出三条快照。随后进入死循环：

```
[14:31:37 WRN] Onboard rejected VehicleBusinessStateSnapshot 2de51bf7-…: SNAPSHOT_REVISION_CONTENT_CONFLICT
[14:31:39 ERR] Journey runtime iteration failed closed; no stage is inferred from memory.
        System.IO.IOException: No recovered Onboard peer is connected.
           at OnboardJourneyPublisher.PublishSnapshotAsync
           at JourneyRuntimeEngine.PublishPickupStateAsync
```

150 秒内会话重建 **22 次**，三条快照一条未被确认，`Stage` 始终停在
`AwaitingPickupArrival`。异常统计：`ProtocolContentConflictException` 51 次、
`IOException` 43 次。

## 根因（**当时的推断，后被证伪**，真因见下面的「修复」）

三个设计选择在重连场景下互相矛盾：

1. 快照的 `messageId` 由旅程状态**决定性派生**，因此跨重连保持不变
   （本次全程为 `2de51bf7-f1be-7a5b-81a9-4de6898edad9`）；
2. `ProtocolOutbox` 冻结首次发送的完整 wire 字节，以实现同 `messageId` 的**逐字节重放**；
   （**这一条今天已不成立**：修复之后 wire 只在同一代次内冻结，代次推进时
   `RefreshOutboundEnvelopeAsync` 会重盖并改写 `PayloadJson`）
3. 协议信封**内嵌 `sessionGeneration`**。

冻结的信封写着 `"sessionGeneration": 21`，而重放发生时活动会话已是第 22 代。对端在新代次收到
一条盖着旧代次、`messageId` 与 revision 均相同但内容不同的快照，按协议回
`SNAPSHOT_REVISION_CONTENT_CONFLICT`；ControlServer 侧对应抛
`ProtocolContentConflictException: Outbound MessageId was replayed with different semantics or a
non-advancing session generation`。

拒绝导致连接结束，下一次 `PublishPickupStateAsync` 因无已恢复对端而抛
`IOException`，旅程迭代 fail-closed；对端随即重连，代次再进一位，重放仍是旧信封——循环因此
自我维持，不会自愈。

## 修复

`ControlServer_MVP@b426ef6`。

**上面那节「根因」的推断是错的**，当时列出的三条可选修法都建立在它之上，因此一条也没有采用。
回归测试当场证伪了那个假设：出问题的不是「冻结的信封盖着旧代次被原样重放」，而是两条互相独立
的缺陷。原推断与三条候选修法原样留在上面，因为定位过程本身有参考价值——错在哪里，比结论更值得
下次读到的人看见。

### 真因一：出站 wire 对端复现不出来

服务端把快照 payload 作为 CLR 对象一次性序列化，`DateTimeOffset` 转换器原样写出时区的 `+`；
对端把行解析为 payload 仍是 `JsonElement` 的信封后重新序列化，该字符经 encoder 转义。JSON 语义
相同、字节不同，而 `AcknowledgeOutboundEnvelopeAsync` 要求逐字节复现，于是到站后第一条
`SnapshotAppliedAck` 就被拒并拆掉连接——现象里车载端那 20 条
`SNAPSHOT_REVISION_CONTENT_CONFLICT` 来自这里。

改法：序列化信封前先把 payload 物化为 `JsonElement`，与协议契约类型自身的构造方式一致。

### 真因二：重放改写了冻结的 `sentAt`

`ReplayPendingForSessionAsync` 用新时钟改写 `sentAt` 与 `CreatedAt`，而快照 payload 的
`observedAt` 是**从 `sentAt` 推导**的（`8caf746` 引入）。重绑后的 payload 因此保留旧
`observedAt`，下一轮发布算出新值，`RefreshOutboundEnvelopeAsync` 判定语义冲突并按「非推进的
会话代次」拒绝，每个运行时迭代各抛一次——现象里那 51 次 `ProtocolContentConflictException`
来自这里，**不是**真因一的字节漂移造成的。

改法：重绑属传输关注点，现在只推进 `sessionGeneration`，`sentAt` 保持冻结。

### 回归测试

`SnapshotWireIsReproducibleByThePeerThatAcknowledgesIt`、
`ReplayIntoANewGenerationLeavesTheWireThePublisherWouldWriteAgain`。两条都建模**对端实际做的
事**；在此之前的测试是拿服务端自己存下的字节去算哈希，那只能证明它和自己一致。

## 影响

修复前：取货到站之后的所有阶段（`AwaitingSublot` 起）无法进入，装货、安全检查、`TO_GATE` 移动
与关卡卸货均不可达。

修复后同一条链路在现场走通。同一台车、同一条 Demand、同样观察时长：

| 指标 | 修复前 | 修复后 |
| --- | --- | --- |
| 会话建立次数（`SessionHello`） | 22 | **1** |
| `ProtocolContentConflictException` | 51 | **0** |
| 车载端 `SNAPSHOT_REVISION_CONTENT_CONFLICT` | 20 | **0** |
| 快照被确认 | 0 / 3 | **3 / 3** |
| `Stage` | `AwaitingPickupArrival` | **`AwaitingSublot`** |

验证时对端为 `OnboardHmi_MVP@304e6ad`（王昆的 `Fix snapshot revision replay across sessions`，
改的正是快照 revision 去重键），证据记录两端修复互通、跨代次后 payload 身份保持不变。

**跨代次重放这条路径本身另有针对性证据**：`20260829-staged-g3-rebind-3d8b00c-304e6ad` 用当时
双端的最新 commit（`ControlServer@3d8b00c`，含本修复）跑重放探针，七条断言全 PASS，其中
`sameMessageIdDifferentContentStableConflict` 与 `recoveryStateReportFirstAckDropReplay` 打的
就是这一块——第 1 代的首个 `DurableAck` 被丢弃并断开，第 2 代在新连接上以同一 `messageId` 与
同一 payload SHA-256 重放并成功收到 Ack，inbox 中该 `messageId` 仅 1 行。

2026-09-03 的现场联调又一次走过同一段并推进到装载环节。L2 的 `normal-load` 场景每次跑都会走一遍
到站快照的发布与确认，但那是**单一会话代次**，不覆盖跨代次重放——那一段由上面的 staged G3 与两条
回归测试承担。

**这条缺陷不再阻塞任何切片。**验证那份证据里 W2G-IS-00～07 的 G3 与 RC 仍记作 `INCONCLUSIVE`，
原因是 `SublotEntryRequested` 在等现场操作员扫码这一人工步骤，与本缺陷无关。

## 相邻已修事项

同次运行还暴露并已修复：ControlServer 此前不支持协议正式定义的 `ProtocolProblem` 消息，
收到即抛异常并拆掉连接，既加剧了重连循环，也丢弃了唯一能说明拒绝原因的诊断。修复见
`ControlServer_MVP@bf22a48`；正是该修复使本缺陷的 reasonCode 得以显现。
