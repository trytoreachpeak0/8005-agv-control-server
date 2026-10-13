# cs#647 证据：途中追加代价打平时同站先卸后装

全部只跑 `Batch7EnRouteAppendPlannerTests` 一个测试类（`dotnet test tests/ControlServer.Tests --filter "FullyQualifiedName~Batch7EnRouteAppendPlannerTests"`），在本票 worktree 里。

## 红与绿

| 文件 | 提交 | 结果 |
| --- | --- | --- |
| `red-753e1b7d.txt` | `753e1b7d`（只有测试，实现之前） | 4 红 26 绿 |
| `green-fe52025d.txt` | `fe52025d`（实现） | 30/30 绿 |

## 变异（`mutations/`）

2026-10-13 19:20:47～19:23:23，在看板本机时段（持有者 cs#647，C11 放行）内跑，基于 `fe52025d`。每处只改 `src/ControlServer.Host/Runtime/Dispatch/EnRouteAppendPlanner.cs` 一个文件，脚本核对「恰好一个文件被改」，跑完 `git checkout` 还原并核对干净。脚本是 `mutations/mutate.sh`，每处的改动是 `<名>.diff`，测试结果是 `<名>.txt`。

| 变异 | 改了什么 | 结果 | 杀死它的用例 |
| --- | --- | --- | --- |
| M1 | 去掉打平偏好，回到修前的严格小于 | 4 红 | 四条修前红的用例 |
| M2 | 偏好用 `<=`，同计数打平也改选后试到的 | 4 红 | `ACrossStationTieStillKeepsTheEarliestPlacement`、既有 `AStopBehindTheCurrentNextOneIsMergedIntoRatherThanDuplicated` 等 |
| M3 | 偏好压过代价（先比计数再比代价） | 1 红 | `AStrictlyCheaperPlacementStillWinsEvenWhenItLoadsBeforeASameStationUnload` |
| M4 | 只数紧挨着的取货→卸货 | 2 红 | `ATiedPickupIsPlacedAfterASameStationUnloadEvenWhenAnotherStopWouldSitBetween` 等 |
| M5 | 不看站号（任意取货在任意卸货之前都算） | 1 红 | `ACrossStationTieStillKeepsTheEarliestPlacement` |
| M6 | 不看角色（同站任意两个停靠都算） | 3 红 | `ATiedPickupAtAStationWithAnUnloadStopIsPlacedAfterTheUnload` 等 |

存活 0。

M6 第一次用常量 `if (false)`，被 `CS0162`（不可达代码，警告当错误）挡在编译期，没跑到测试；19:22:58～19:23:23 改用运行期假条件 `stops.Count < 0` 重跑，`mutate.sh` 里留的是重跑的版本。
