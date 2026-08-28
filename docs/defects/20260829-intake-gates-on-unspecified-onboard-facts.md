# 缺陷：受理门禁建立在协议未定义的车载事实之上

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: [`零 mutation 双端演练`](../../evidence/g3/20260829-zero-mutation-peer-rehearsal/SUMMARY.md)
Product at discovery: `ControlServer_MVP@1a0158c87c36fb2e3e0f4ddca1f7ed9c84d5672b`
Peers: `OnboardHmi_MVP@84b7f3f`、`slots-simulator@fb5f7c5`、`protocol-v0.1.1@1531489`

两条缺陷都在 `JourneyRuntimeEngine.ReadOnboardFactsAsync` 中，都会让**每一条** WIRE_TO_GATE
Demand 判为 `ONBOARD_FACTS_NOT_READY`，从而在现场完全无法受理。两条的归属都在本仓库：车载端
的行为与协议一致，是服务端单方面加了协议没有的要求。

---

## D-1：把协议未定义的 `supportsBatchUnlock` 当作硬准入门禁

### 现象

`ReadOnboardFactsAsync` 在

```csharp
!capabilityPayload.GetProperty("supportsBatchUnlock").GetBoolean()
```

时返回 `null`，`ValidateDynamicFacts` 随即对所有候选返回 `ONBOARD_FACTS_NOT_READY`。演练中
车载按其出厂配置发送 `supportsBatchUnlock=false`，于是 12 条 WIRE_TO_GATE 中可受理的 4 条
全部被挡，一条都进不了旅程。

### 为什么归属在服务端

- `protocol-v0.1.1` 的 `CapabilitySnapshot.schema.json` 把 `supportsBatchUnlock` 定义为一个
  **没有任何 description 的裸 boolean**；协议的 `docs/`、`integration-slices/`、`errors/`
  中没有一处定义它的语义。
- 协议自己的合法样例 `V-CapabilitySnapshot-MIN-001.json` 取值就是 `false`。
- 车载端对该字段没有任何行为分支：它只在 `Configuration.cs` 中被读取、在
  `WireToGateSessionClient.cs` 中被抄进快照。配 `false` 与协议样例一致，不构成违约。
- 本仓库是唯一把它当作行为门禁的一方，且只有这一处使用。

### 车载实际具备该能力

`WireToGateSlotOperationExecutor` 明确支持多仓位命令：

- `ValidateCommand` 接受 `Slots.Count` 为 **1～8**，要求去重且升序；
- `ValidateBeforeOperation` 遍历命令中的每个仓位逐一校验；
- `ExecuteExclusiveAsync` 以 `foreach (int physicalSlot in command.Slots)` 串行执行，每个
  仓位单独写 journal 检查点、脉冲解锁、等待锁反馈与输出复位、等待操作员；
- `CreateRejectedResult` 按仓位构造结果数组。

即车载支持"一条命令覆盖多个仓位"，只是逐门串行而非多门同时开启（与模拟器
`maxOpenDoors: 1` 一致）。关卡批量卸货所需的能力是前者。

### 该门禁本身是冗余的

服务端在真正下发 `SlotOperationCommand` 之前，已在 `FinalDynamicFactsReadyAsync` 中校验
`targetSlots` 全部包含于 `onboard.AvailableSlots`；车载侧 `ValidateCommand` 亦会拒绝非法
仓位集。因此"车辆是否能执行这批仓位"在发送时点已有基于真实事实的检查，受理时点再用一个
未定义的布尔量重复把关，只会带来假阴性。

### 建议修复

移除 `supportsBatchUnlock` 作为受理前置条件，保留发送时点基于 `AvailableSlots` 的真实校验。
若确需一个能力协商位，应先在协议中定义其语义并由两名负责人批准发布，再由两端按定义实现；
在此之前不得以该字段做阻断决策。

---

## D-2：对"按变化通知"的事实套用轮询新鲜度阈值

### 现象

`ReadOnboardFactsAsync` 要求 `CapabilitySnapshot` 与 `SafetyStateSnapshot` 的 `observedAt`
都新于 `JourneyRuntime:maximumEvidenceAge`（当前 30 秒），否则返回 `null`。

演练观测：两个快照各只在会话建立时发送**一次**，此后车载只发周期 `Heartbeat`（一次运行
2 分钟 23 个、另一次 1 分钟 11 个），未再重发快照。

由此，**一个会话的受理窗口只有建立后的前 30 秒**。演练中受理之所以成功，是因为它发生在
快照后 2.4 秒（快照 `16:44:16.886` / `16:44:16.930`，受理 `16:44:19.305`）；会话持续到
`16:45:12` 期间不再有新快照，此后该会话已无法受理任何 Demand。

### 为什么归属在服务端

- 协议对 `CapabilitySnapshot` 与 `SafetyStateSnapshot` **没有任何发送节奏或新鲜度要求**；
  `docs/`、`integration-slices/index.json` 中均无 cadence、freshness、observedAt 相关约定。
- 车载每会话发送一次不违反任何协议条款。
- 30 秒阈值是本仓库自行引入的，且被套用在一个由 `SafetyStateChanged` 做变化通知的事实上——
  演练中确实收到过一条 `SafetyStateChanged`，说明该通知通道是存在且工作的。

### 建议修复

把车载能力与安全事实按**会话作用域**处理：会话建立时取得，由后续
`SafetyStateSnapshot` / `SafetyStateChanged` 取代，会话结束或 generation 变化时失效，不再
按时间过期。`maximumEvidenceAge` 继续适用于真正靠轮询获得的 RIoT 车辆观测
（`ValidateDynamicFacts` 中的 `RIOT_VEHICLE_FACT_STALE` 分支），该处用法是正确的。

---

## 修复

`ControlServer_MVP@9a42582654d7ca793499556522beb1547403fafb`（D-1／D-2 落地于 `74ddda2`，
其自身引入的活性扫描缺陷修正于 `9a42582`）。出厂车载配置下的验证见
[`受理门禁修复验证`](../../evidence/g3/20260829-intake-gate-fix-verification/SUMMARY.md)：
受理数由 0 变为 1，旅程推进到 `AwaitingPickupArrival` 并停在 `PICKUP_CreateDispatchDisabled`，
运行 2 分 35 秒后仍在正常推进，证明 30 秒受理窗口已解除。

## 影响

在这两条修复之前，现场无法进行任何真实受理，因而也谈不上真实建单或移动闭环。W2G-IS-00～07
的正式 G3 与 RC 保持 `INCONCLUSIVE`。
