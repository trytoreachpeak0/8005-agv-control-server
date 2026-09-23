# cs#345 真装置 L2

本机真装置时段（调度 Coordinator 8 于 2026-09-24 02:18 放行，02:40 归还），真车载端 WPF + 真 slots-simulator。
对端都是分离 worktree：车载端 `1184bb0762462f443736de749fe242fc9aa2f0d7`（`w2g/fp-v2-impl` 顶端），
模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端），每遍都显式传 `-OnboardRepository`、`-SimulatorRepository`。

| 场景 | 服务端提交 | runId | 结论 | 判据 |
| --- | --- | --- | --- | --- |
| `real-onboard-rebuild-stopped-cargo-handoff`（新） | `ab52333f`（分支） | `20260923T181903755Z` | PASS | L2-RH-01～11 全 PASS |
| `real-onboard-rebuild-stopped-cargo-handoff`（新） | `bb71e356`（本地变异，不推送） | `20260923T182047691Z` | FAIL | 01～06 PASS；07、08 FAIL；09～11 未到达 |
| `g3-fault-cargo-handoff`（回归） | `ab52333f`（分支） | `20260923T182450216Z` | PASS | G3-07-31～36 全 PASS |

## 独立审查修改之后，在新 head 上重跑

审查 M1 改了衔接 b 的就绪判据与转交接（`f848101c..494cf51e`，含一次合入集成分支），所以调度要求在新 head 上重跑新场景与 CI 回归。
车载端换成 `w2g/fp-v2-impl` 新顶端 `f31ca2b76f72692b63a69b015858eb661d3cdb65`（hmi#204 合入，改了恢复与补发路径），模拟器不变。
对端缓存放在 `C:/w2g/cs345-peers`（`-PeerCacheRoot`）：本会话里 `%LOCALAPPDATA%` 被桌面应用虚拟化，在那里编车载端报 CS2001
（找不到分明存在的 `.editorconfig`）。

| 场景 | 服务端提交 | runId | 结论 | 判据 |
| --- | --- | --- | --- | --- |
| `real-onboard-rebuild-stopped-cargo-handoff`（本机时段，调度 09-24 04:16 放行） | `494cf51e` | `20260923T201726824Z` | PASS | L2-RH-01～11 全 PASS（`green/real-rig-real-onboard-rebuild-stopped-cargo-handoff-494cf51e/`） |
| `real-onboard-compensate-then-reconnect`（CI 真装置） | `494cf51e` | run 35914899283 | PASS（79 秒） | 场景那一步读到三端 `494cf51e`／`f31ca2b7`／`fb5f7c59`；`RIG_*` 停机码只在源码回显里；没有停机（汇总 1 runs） |

## 红证据：衔接 b 的变异

变异只改一处：`WireToGateStore.DecideReadinessAsync` 里「这辆车有 Blocked、码为 `OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF`
的旅程」那一项换成 `false`（diff 见 `red/real-rig-real-onboard-rebuild-stopped-cargo-handoff-mutation-b-bb71e356/mutation-b.diff`），
基于 `ab52333f` 做本地提交，替换点恰好命中 1 处，构建 0 Warning(s) 0 Error(s)。红只落在要验的那一格：

- `L2-RH-07` FAIL，实际 `Ready/READY`：转交接之后服务端没有向车宣布会话要恢复。
- `L2-RH-08` FAIL，实际「90 秒内没出现」：车载端只在 `RECOVERY_REQUIRED` 时给「故障交接」入口。
- `L2-RH-09`～`11` 记为未到达（没有入口就走不到交接）。

**为什么别的判据不红。**`L2-RH-01`～`06` 是服务端自己的库状态与入口应答：记故障、清除、货不在原仓停住、人工重建被拒、
转交接把旅程挂起、重复请求 AlreadyDone，都不经过就绪判定，变异碰不到。挂起（`L2-RH-05`）与就绪（`L2-RH-07`）拆成两条判据，
正是为了让这一处变异的红指到「服务端没宣布」而不是「服务端没挂起」。

完整证据（日志、库快照）只在本机保留，入库的是每遍的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。
