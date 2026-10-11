# 负载下连跑（部分，2 轮）

- 跑法：`evidence/cs256/load/Run-LoadRounds.ps1`，经 `Invoke-HeavyLocal.ps1 -Ticket cs#256` 低优先级启动。每轮 8 个 `dotnet test` 进程同时跑 `OnboardSilentLivenessLossTests`：4 份改后（`new1..4`，14 条）+ 4 份改前（`old1..4`，`origin/fp/v2-impl` 的旧夹具，10 条）。改前那 4 份照旧「探测后释放端口」，给改后那 4 份制造真实的端口争抢。
- 产物放在 worktree 的 `tests/ControlServer.Tests/bin/cs256-load/` 下（被 gitignore）：放到仓外时程序集收尾的 schema 检查找不到 `ControlServer.sln`，会报 `Test Assembly Cleanup Failure`，与夹具无关。
- 启动：2026-10-11 11:38:55（包装），第一份测试 11:40:50。
- 结果：第 1、2 轮共 16 份，全部 `Passed!`、退出码 0（改后 8 份 14/14，改前 8 份 10/10）。
- 中止：调度 10-11 通知本机重测试改为同一时间只放一个会话，第 2 轮跑完即停；第 3 轮在停止时刚开头、被中断，不计入，日志未收。剩余轮次等时段放行后补跑。
