# 缺陷：跨会话代次重放快照造成自我维持的重连死循环

Status: open
Owner repository: `8005-agv-control-server`
Found by: [`到站推进首次可达后的联合运行`](../../evidence/g3/20260829-placeholder-fix-confirms-leg/SUMMARY.md) 之后的到站验证运行
Product at discovery: `ControlServer_MVP@bf22a48d5d0e386e7af21d162800f7cf09b0032d`
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

## 根因

三个设计选择在重连场景下互相矛盾：

1. 快照的 `messageId` 由旅程状态**决定性派生**，因此跨重连保持不变
   （本次全程为 `2de51bf7-f1be-7a5b-81a9-4de6898edad9`）；
2. `ProtocolOutbox` 冻结首次发送的完整 wire 字节，以实现同 `messageId` 的**逐字节重放**；
3. 协议信封**内嵌 `sessionGeneration`**。

冻结的信封写着 `"sessionGeneration": 21`，而重放发生时活动会话已是第 22 代。对端在新代次收到
一条盖着旧代次、`messageId` 与 revision 均相同但内容不同的快照，按协议回
`SNAPSHOT_REVISION_CONTENT_CONFLICT`；ControlServer 侧对应抛
`ProtocolContentConflictException: Outbound MessageId was replayed with different semantics or a
non-advancing session generation`。

拒绝导致连接结束，下一次 `PublishPickupStateAsync` 因无已恢复对端而抛
`IOException`，旅程迭代 fail-closed；对端随即重连，代次再进一位，重放仍是旧信封——循环因此
自我维持，不会自愈。

## 可选修法（尚未定夺）

1. **把 `messageId` 纳入会话代次**：新代次派生新的 `messageId`，旧信封自然作废。逐字节重放
   语义保留在代次内部。代价是需确认 Onboard 侧对同一业务快照换 `messageId` 的处理。
2. **重放时重新盖章**：保留 `messageId` 但按当前代次重建信封。这会打破"同 `messageId` 逐字节
   重放"这一既有不变量，且该不变量本身有其防重目的。
3. **禁止跨代次重放快照**：`ReplayPendingForSessionAsync` 已接收 `sessionGeneration` 参数，
   说明原意可能就是只在同代次内重放；需查清为何仍发出了旧代次信封。

选项三最接近既有设计意图，应先查明实际重放路径再定。三者都需要确认对端在新代次下期望
看到什么，必要时以 `ProtocolProblem` 的 reasonCode 作为判据。

## 影响

取货到站之后的所有阶段（`AwaitingSublot` 起）目前无法进入，因此装货、安全检查、`TO_GATE`
移动与关卡卸货均不可达。W2G-IS-00～07 的正式 G3 与 RC 保持 `INCONCLUSIVE`。

## 相邻已修事项

同次运行还暴露并已修复：ControlServer 此前不支持协议正式定义的 `ProtocolProblem` 消息，
收到即抛异常并拆掉连接，既加剧了重连循环，也丢弃了唯一能说明拒绝原因的诊断。修复见
`ControlServer_MVP@bf22a48`；正是该修复使本缺陷的 reasonCode 得以显现。
