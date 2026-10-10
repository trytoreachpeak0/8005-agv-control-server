# cs#345 本机合成 L2

经 `Invoke-HeavyLocal.ps1 -Ticket cs#345` 逐条跑 `scripts/l2/Invoke-L2Scenario.ps1`（合成车载端，不是真装置）。

| 场景 | 服务端提交 | runId | 结论 | 判据 PASS / FAIL |
| --- | --- | --- | --- | --- |
| `in-transit-rebuild-stopped-person-rebuilds`（新） | `012c31b2`（修复前，红证据） | 见 `red/l2-in-transit-rebuild-stopped-person-rebuilds-012c31b2/` | FAIL | 1 / 4 |
| `in-transit-rebuild-stopped-person-rebuilds`（新） | `4694e43b` | `20260923T170943430Z` | PASS | 5 / 0 |
| `in-transit-rebuild-stopped-person-rebuilds`（新） | `756281a6` | `20260923T173029624Z` | PASS | 5 / 0 |
| `in-transit-order-cancelled-rebuilt` | `756281a6` | `20260923T173152550Z` | PASS | 5 / 0 |
| `vehicle-fault-operator-clearance` | `756281a6` | `20260923T173308195Z` | PASS | 8 / 0 |

完整证据只入库新场景在 `756281a6` 上那一轮（`l2-in-transit-rebuild-stopped-person-rebuilds-756281a6/`），其余只记 runId。

红证据的跑法：修复前的 `fp/v2-impl`（`012c31b2`）开一个不含 `evidence/` 的分离 worktree，把它的 `scripts/` 复制到临时目录、
加进本场景两份文件，用副本的 `Invoke-L2Scenario.ps1 -Repository <那个 worktree>` 跑。

同一批里还误带了真装置场景 `g3-fault-cargo-handoff`：没有传对端路径，它在发布对端那一步就以
`Peer repository not found` 失败，没有拿桌面锁、没有起车载端窗口。这条不算回归结果，真装置回归另按调度安排跑。
