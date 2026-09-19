# 车载端全量 L1（批次 6 出口）

- 提交：`8005-agv-onboard-hmi@44b3aa6e`（`w2g/fp-v2-impl` 顶端，含 onboard-hmi#127），detached worktree
- 命令：`dotnet test ./SQCD_8005AGV.sln -c Release --logger trx`，经 `Invoke-HeavyLocal.ps1 -Ticket cs#165`，control-server#165 封锁时段内
- 结果：**623 / 623 通过**，0 失败 0 跳过——`SQCD.Agv.UnitTests` 395、`SQCD.Agv.WireToGateG2Tests` 228
- 原文与 TRX：`dotnet-test.log`、`trx/`
