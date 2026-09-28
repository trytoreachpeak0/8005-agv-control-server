cs#342 断线重连模型测试（ReconnectModelTests.PrototypeMeasurement，explicit only），CS342_ITER=300，masterSeed=342，Release 构建，
经 Invoke-HeavyLocal 跑，命令末尾 `-- xUnit.Explicit=on`。
- report-6e10044e.txt：本票头 6e10044e（门锁症状、选项 X、A 的自动解除都在内）。测试退出码 0。
- 对照：cs#366 在同一模型、同一种子下为集成分支基点 d81c78ad 跑的 evidence/cs366/cs342-model/report-prefix-engine-d81c78ad.txt
  （提交 9e771922）。本票的基点也是 d81c78ad，所以直接拿它当对照，没有重跑。
两份报告除耗时三行外逐行相同（diff 去掉含 ms 与 wall 的行后为空）：300/300 录入请求送达车上，ack 冲突 0，车辆倒退 0，
握手中与握手后都没有接受不同内容的同号消息；「带着等人码的轮次失败」两边都是 5 次（ORDER_HANG 5），集成分支上原有。
模型里的车载端永远报门锁锁着，所以它只能证明本票没改坏断线重连这一块，证明不了本票的新行为；新行为由
InTransitDoorLockFaultTests、InTransitDoorEmergencyReleaseTests 与 L2 场景 in-transit-door-not-locked、real-onboard-in-transit-door-facts 证。
