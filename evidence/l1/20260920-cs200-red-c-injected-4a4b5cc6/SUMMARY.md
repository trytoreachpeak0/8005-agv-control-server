# cs#200 c 红证据：注入故障「unattributed 一律回墓碑」

- 提交：`4a4b5cc6`，在 `fa9acfa4`（c 的测试）之上的临时本地提交，跑完即撤，未推送；改动见 `injected-fault.diff`：去掉 `ReconcileAsync` 回墓碑分支里的 `pointer.ActiveVersion is null`。
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter ` 新的两条 c 测试 + 既有两条 `unattributed` 测试（`AnInterruptedActivationOfAnEmptyRequirementSet…`、`AnInterruptedFirstActivationOfAnEmptyRequirementSet…`）
- 结果：`Failed: 1, Passed: 3`。
  - 红：`AnUnattributedAttemptOverAnActiveVersionThatDidNotHappenGoesBackToActiveNotToATombstone`，`Expected: "25|2|ACTIVE|<null>"`，`Actual: "25|2|CLOSED_MANUALLY|<null>"`。
  - 绿：既有两条 `unattributed` 测试（它们本来就回墓碑）。
  - 绿：`AnUnattributedAttemptWhoseTargetCommittedGoesToActiveOnTheTarget`。这条走的是「目标已生效」结论，被注入的是「原版本仍在用」那条分支，所以这一注入碰不到它；它守的是另一条路。
