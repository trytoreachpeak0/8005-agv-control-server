# control-server#339 合成 L2：`station-deadline-sublot-timeout` 的 `L2-SD-16`

`L2-SD-16`：重连之后，车收到并确认了一版号更大、`stationDepartureDeadlineAt` 等于重填后期限的清单。合成车载端，本机经
`Invoke-HeavyLocal.ps1` 跑，每目录只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。

| 目录 | 服务端提交 | 结果 |
| --- | --- | --- |
| `green-d01e68c6` | `d01e68c6`（本票分支） | PASS，16 条全过。`L2-SD-16` 实际：`r3 …15:49:12.246 ack=True, r4 …15:49:23.219 ack=True`，r4 的期限 = 重填起点 `15:49:03.219` + 20 秒 |
| `red-d64a15c1` | `d64a15c1`：`d01e68c6` 的场景脚本 + 本地变异提交 `55f861d3`（去掉升版、清单沿用改回忽略期限，即 cs#331 合入时的样子；只留本地，不推送） | FAIL，16 条里只有 `L2-SD-16` 红。实际：`r3 …15:50:55.739 ack=True`——车上只有到站那一版，带的是到站起点 `15:50:35.739` + 20 秒；服务端已重填为 `15:50:46.291` |
| `red-v1-55f861d3` | `55f861d3`（场景脚本是挪动之前的版本） | FAIL，`L2-SD-16` 与 `L2-SD-12` 同时红。`L2-SD-12` 是陪红：修前 `L2-SD-16` 要等满 30 秒，那时它在 `L2-SD-12` 之前，把「原期限过去 3 秒、新期限之前」那一读推到了新期限之后。场景因此把 `L2-SD-16` 挪到 `L2-SD-12` 之后（`d01e68c6`），重跑的红只落在 `L2-SD-16` 上 |

变异提交本身没有推送、跑完已删，所以它们改了什么存在这里（PR #353 审查 S5，2026-09-24 从本机仍在的对象导出）：

- `mutation-55f861d3-vs-523d0723.diff`：`git diff 523d0723 55f861d3`，变异提交相对它的父提交，只动 `JourneyRuntimeEngine.cs` 两行——清单沿用改回忽略
  `stationDepartureDeadlineAt`，升版判据改成「排过就不升」。
- `red-d64a15c1` 用的是同一处变异：`d64a15c1` 相对本分支的 `d01e68c6`，在 `src`、`tools`、`scripts`、`.github` 下的差异与上面这份改动行逐行相同
  （导出在 `../20260923-cs339-rig/mutation-d64a15c1-vs-d01e68c6.diff`）。两者在 `tests/` 与 `evidence/` 下另有差异，是 `d64a15c1` 的祖先里没有
  零变化基线那个提交 `4c62c74e`；L2 只构建服务端与工具，不读这两处。
