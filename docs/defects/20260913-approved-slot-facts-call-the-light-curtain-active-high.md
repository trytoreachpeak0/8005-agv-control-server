# 缺陷：服务端的已批准仓位硬件事实把光幕写成高有效，票据 35 批准的是低有效

Status: open（待产品负责人决定处置，见最后一节；W1 的 `seed-approved-facts` 之前要定）
Found by: 为 W1 现场核对写逐仓 IO 探针时核对极性，2026-09-13
Product at discovery: `ControlServer_MVP@981fce35`（生产 `3b379bb` 同样如此）；`fp/v2-impl` 线同一个常量

---

## 现象

`src/ControlServer.Domain/SlotConfigurationModels.cs:75`：

```csharp
public const string SignalPolarity = "ACTIVE_HIGH";
```

`ApprovedSlotHardwareFacts.IoBindings` 用这一个值填满八个仓位的 `SlotIoBindingSpecification.SignalPolarity`，也就是开锁输出、
锁反馈、仓内光幕三种信号共用「高有效」。

票据 35（`8005-agv-program/.scratch/current-requirements-baseline/issues/35-supply-slot-hardware-and-field-signal-evidence.md:15`）
批准的事实不是这样：

| 信号 | 票据 35 | 与「高有效」一致？ |
| --- | --- | --- |
| 开锁 DO | 写 `1` 开锁，模块 500 ms 脉冲复位为 `0` | 一致 |
| 锁反馈 DI1～DI8 | 锁闭 `1`，打开 `0` | 按「有效＝锁闭」读一致 |
| 仓内光幕 DI9～DI16 | **检测到物体 `0`，未检测到 `1`** | **不一致**：有效（有物）是低电平 |

REQ-0267 的规范文本把极性明确交给票据 35（「500 ms 脉冲复位及票据 35 已批准的信号极性和机构事实为准」）。

同一事实在别处都按票据 35 实现：

- 车载端 `SQCD.Agv.Core/DomainModels.cs:97,100`：`IsLocked => LockFeedbackRaw is true`，`HasCargo => LightCurtainRaw is false`
- 模拟器 `simulator.settings.json`：`lockedActiveLevel: 1`，`obstructedActiveLevel: 0`

## 影响

- **W1 当天的 `seed-approved-facts` 会把这个值写进生产库。**它走版本化治理发布路径，写进去就不可改写，事后只能发新版本、保留旧版本。
- `SlotConfigurationAuthorityStore.cs:266` 用它比对车载端声明的 `SignalPolarity`；`SlotConfigurationActivationCoordinator.cs:76`
  把它放进激活下发。在 v2 线上这条比对属于 FP-IS-14 的面，改这个常量会动到那片的证据。
- 不影响真实开锁与读 IO：车载端按自己的配置与 `DomainModels` 解释电平，不读这个字段。
- W1 探针（`scripts/field/Invoke-W1SlotIoProbe.ps1`）按票据 35 判定，不按这个常量，所以现场核对结论不受它误导。

## 处置选项（未决）

1. **先改后 seed。**把极性表达成逐信号的值（例如锁反馈 `LOCKED_HIGH`、光幕 `OBJECT_LOW`），两条线一起改，补测试；
   v2 线上 FP-IS-14 的相关证据要重跑。W1 的 seed 用改过的 FieldOps。
2. **照现值 seed，记账。**W1 证据与出口报告写明这个字段与票据 35 不符、实际核对按票据 35；以后按选项 1 改完再发第二版硬件事实。

在产品负责人决定之前，不要在生产库上跑 `seed-approved-facts`。
