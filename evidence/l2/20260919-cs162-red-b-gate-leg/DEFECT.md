# 红证据：缺陷版本 gate-leg-ignores-hold

注入的缺陷：建单门不查暂停：GateLegAsync 里 holds.Holds(taskType) 的判断永远不成立（JourneyRuntimeEngine）。

缺陷提交 `e77db781` 在本地临时分支 `tmp-cs162-red-gate-leg-ignores-hold` 上，基于 `d223b14e`，未推送、用后删除。
判定与失败原因见同目录 `SUMMARY.md` 与 `assertions.json`。
