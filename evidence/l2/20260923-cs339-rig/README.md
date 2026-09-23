# control-server#339 笔记本真装置：`real-onboard-refilled-deadline-reaches-vehicle`

2026-09-24 00:14 之后，调度 Coordinator 8 放行的笔记本时段。开跑前实读：桌面锁空闲、重负载 0 个。对端都是 detached worktree：
车载端 `1184bb0762462f443736de749fe242fc9aa2f0d7`（`w2g/fp-v2-impl` 顶端），模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端），
两者都 `ls-remote` 核过。每个目录只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。

| 目录 | 服务端提交 | 结论 |
| --- | --- | --- |
| `red-d64a15c1` | 本地变异 `d64a15c1`（本分支场景脚本 + 去掉升版、清单沿用改回忽略期限；只留本地，未推送，跑完已删） | FAIL，红证据。前提 `L2-RD-01`～`03` 全 PASS；`L2-RD-04` FAIL，预期「车上 = 服务端 = 2026-09-23T16:20:32.1642209+00:00（到站那一个是 2026-09-23T16:20:24.4281831+00:00）」，实际「车上 r1 2026-09-23T16:20:24.4281831+00:00 / 服务端 2026-09-23T16:20:32.1642209+00:00」；`L2-RD-05` FAIL（r1，预期 > r1）；`L2-RD-06` PASS（变异下录入请求也没升版，与车上那一版一致） |
| `precheck-c0c5aa81-script-threw` | `c0c5aa81`（分支顶端） | 六条断言全 PASS，结论 FAIL：场景脚本最后一行 `@(Get-ServerWorklists) \| ForEach-Object` 把整张列表当一个元素，两版清单时抛 `Cannot find an overload for "ToString" and the argument count: "1"`。只有一版时成员枚举恰好给出标量，所以第一遍没抛。`33f7e7ac` 改为先赋值再遍历，判据未动 |
| `precheck-33f7e7ac` | `33f7e7ac` | PASS，六条全过；`L2-RD-04` 车上 r2 `16:25:57.6568715` = 服务端 `16:25:57.6568715`。只是预检，不算绿证据——绿证据是最终 head 上的 CI 真装置 |
