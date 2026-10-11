# 负载下连跑（放行时段内，8 轮）

- 时段：调度 10-11 放行，持有者行为 `cs#256 封锁`，本机时段只此一个会话使用。
- 跑法：`evidence/cs256/load/Run-LoadRounds.ps1 -Rounds 8`，经 `Invoke-HeavyLocal.ps1 -Ticket cs#256` 启动；每轮前读 C 盘，低于 15 GiB 即停（全程 26.8 GiB，未触发）。
- 时间：2026-10-11 12:21:58 至约 12:31:40，每轮 67～80 秒。
- 结果：64 份测试类运行，改后 32 份每份 14/14、改前 32 份每份 10/10，退出码全 0。见 `summary.txt` 与 `progress.txt`。
- 与 `../partial-2-rounds/`（与 `hmi#285-review` 并发跑的 2 轮、16 份）合计 10 轮、80 份。
