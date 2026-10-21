# control-server#827 目录时间窗（REQ-0366）— L1 证据

| 目录 | 内容 |
| --- | --- |
| `red/01-tests-at-d0430730d.txt` | 测试提交（只有桩，判据恒放行、受理前不重判）上跑 `CatalogAgeWindowTests` 与 `StructuralDispatchBlockTests`：14 条红，包括 31 分钟那条（去掉判据与重判，超窗的被接走） |
| `green/01-affected-classes-and-architecture.txt` | 实现加 merge `origin/fp/v2-impl`（`2c0f6616a`）后，相关测试类与全部架构测试：783 条全过 |
| `green/02-parallel-instance-selftest.txt` | `scripts/parallel/Test-ParallelInstance.ps1`：1320 条全过，含 8 条时间窗用例 |
| `green/03-format.txt` | 改动文件的 `dotnet format --verify-no-changes`：只有 `DispatchRoundRunner.cs` 既有行上的 14 处，本票新增行为零；后改的三个文件 exit 0 |
| `mutations/` | 9 个变异，每次只改一个文件、跑完 `git checkout` 还原，全部变红；脚本 `run-mutations.ps1` |

变异对照：

| 变异 | 改了什么 | 变红的用例 |
| --- | --- | --- |
| M1 | 从准入链去掉判据 | 依赖「开轮即挡」的 5 条（放回待派的对照、两条看板、阻断清除、防饥饿）。31 分钟那条仍绿：受理前重判是第二道防线，两道都去掉才红（见 `red/`） |
| M2 | 受理前不重判 | `TheWindowIsJudgedAgainWhenIntakeRereadsTheCatalog` |
| M3 | 判据序号 45 → 13 | `Batch7Req0185EutecticExclusionTests` 两条：共晶等不归本线的旧需求改报时间窗码，从静默挪进看板 |
| M4 | 结构性汇总不把两码当静默 | `ADemandPastTheWindowRaisesNoStructuralBlockAndLosesTheOneItHad` |
| M5 | 防饥饿排除集合不排超窗的 | `ADemandPastTheWindowIsNeverEscalatedForStarvation` |
| M6 | 放回待派不豁免 | `ADemandReleasedBackForRedispatchIsRedispatchedWhateverItsAge` |
| M7 | 边界 `>` 改 `>=` | `ADemandExactlyAtTheWindowIsStillDispatched` |
| M8 | 缺 `createdAt` 放行 | `ADemandWithoutAFirstReadTimeIsNotDispatchedAndWaitsWithItsOwnReason` |
| M9 | 轮次不取配置值 | `TheWindowIsTheConfiguredOne` |
