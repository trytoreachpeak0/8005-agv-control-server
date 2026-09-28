cs#342 断线重连模型测试（ReconnectModelTests.PrototypeMeasurement，-explicit only），CS342_ITER=300，masterSeed=342，Release 构建，经 Invoke-HeavyLocal 跑。
- report-476d6988.txt：本票头 476d6988（产品代码与 23711d51 相同）。测试退出码 0。
- report-prefix-engine-d81c78ad.txt：对照。同一 worktree 里把 JourneyRuntimeEngine.OwnOrderRebuild.cs 与 OwnOrderRebuilds.cs 临时换回 d81c78ad
  （新列保留、不被读取），同样 300 个种子。测试退出码 0。之后用备份还原，git status 干净，并按本票代码重新构建 Release。
  （另开 d81c78ad 的 detached worktree 时检出失败：Could not reset index file，没有留下目录，所以改用这个做法。）
两份报告除耗时外逐行相同：300/300 录入请求送达车上，ack 冲突 0，车辆倒退 0，握手中与握手后都没有接受不同内容的同号消息；
「带着等人码的轮次失败」两边都是 5 次（ORDER_HANG 5），即集成分支上原有，不是本票带来的。
模型测不到取消与故障动作（cs#342 的「模型的局限」），所以它只能证明本票没改坏断线重连这一块，证明不了本票的新行为。
