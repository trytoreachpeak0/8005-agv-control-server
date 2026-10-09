# control-server#532 证据：审查 S1 用例与变异 M2

用例提交（本文件同一提交）在 `78ca8df8` 之上，只改测试。命令都是：

```
dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter "<见下>"
```

## 新用例绿

过滤 `FullyQualifiedName~RecoveryEndingReleasesBlockedJourneyTests|FullyQualifiedName~AdministratorClose|FullyQualifiedName~ArchitectureTests`，退出码 0：

```
Passed!  - Failed:     0, Passed:   139, Skipped:     0, Total:   139, Duration: 1 m 34 s
```

其中 S1 是 `RecoveryEndingReleasesBlockedJourneyTests.ADoorUnprovenCompensationReleasesTheBlockedJourneyButTheDoorHoldKeepsTheVehicle`，
S2 是 `RecoveryStateMachineG2Tests.AdministratorClose*` 里新加的 `closedReason` 断言。

## 变异 M2（手工，跑完即还原）

改法（审查原文）：在放回成功时，把本次刚写下、还没保存的扣车行删掉。落在 `OnboardRecoveryCoordinator.cs`
已证明全空收尾里 `BlockedJourneyRelease.StageAsync` 返回 `true` 的分支，`LogBlockedJourneyReleased` 之后加一行：

```csharp
foreach (var m2 in dbContext.ChangeTracker.Entries<SlotDoorHoldRow>().Where(e => e.State == EntityState.Added).ToList()) m2.State = EntityState.Detached;
```

`--no-incremental` 重编，`0 Error(s)`。过滤 `FullyQualifiedName~RecoveryEndingReleasesBlockedJourneyTests|FullyQualifiedName~RecoveryStateMachineG2Tests.RecoverySurface`：

```
[FAIL] ControlServer.Tests.RecoveryEndingReleasesBlockedJourneyTests.ADoorUnprovenCompensationReleasesTheBlockedJourneyButTheDoorHoldKeepsTheVehicle
Failed!  - Failed:     1, Passed:    49, Skipped:     0, Total:    50, Duration: 1 m 51 s
```

单跑这一条，报错原文：

```
System.InvalidOperationException : Sequence contains no elements.
```

即 `SlotDoorHolds.SingleAsync` 找不到扣车行：变异让扣车没入库。其余 49 条在变异下全绿，与审查「现有用例在 M2 下全绿」一致，
所以 M2 只靠这条新用例杀。

还原用 `git checkout --` 恢复源文件（`git status` 干净，`MUTATION M2` 计数 0），`--no-incremental` 重编 `0 Error(s) 0 Warning(s)`，
单跑这一条：

```
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1
```

## 不在本证据里

按 10-09 起的流程不跑本机全量，由转 ready 后那一轮 CI 充当。真装置由调度与 hmi#282 合派一轮。
