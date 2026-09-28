# 注入 M8 的红证据（判据 L2-DL-10 的判别力）

两路合并审查建议：合成场景行驶时把车摆到两站之间（`currentPosition = 0`），否则 L2-DL-10「解除之后不再急停」看不出豁免那条规则。
改完（提交 `9fef57d2`）之后，临时注入变异 M8——门锁原因解除后不再豁免「报不出站点」（`evidence/cs335/mutations.md`）——跑一次本场景。

预期只有 L2-DL-10 红。实际：结论 FAIL，唯一不通过的判据是 L2-DL-10，实际值 `2 trigger / SuspectedBlocked`（解除之后又急停了一次）。
其余 10 条照常通过。注入前备份了 `VehicleFaultCoordinator.cs`，跑完从备份还原，`git diff -- src` 为空。
同一提交上不注入的那一轮是绿的：`20260928-cs335-in-transit-door-not-locked-9fef57d2`。
