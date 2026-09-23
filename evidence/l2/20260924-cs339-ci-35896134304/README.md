# control-server#339 CI 真装置 run `35896134304`（审查修改之后）

head `d742f0ae`。三端从「Run real-onboard L2 scenarios」那一步读为 control-server `d742f0ae999109f1b8b6776f013b6ea2bda3d7bf`、
onboard `1184bb0762462f443736de749fe242fc9aa2f0d7`、simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`；`RIG_COMMIT_WAITING`／`RIG_DESKTOP_LOCK`
只有 2 处命中，都带字面 `^[[36;1m`（源码回显），不带前缀的 0 处；没有停机。整轮汇总在 `run-SUMMARY.md`。

| 场景 | 结果 | 说明 |
| --- | --- | --- |
| `real-onboard-refilled-deadline-reaches-vehicle-01` | PASS 39s | 7 条全过。`L2-RD-04` 车上 r2 `17:41:59.3767749` = 服务端，到站那一个是 `17:41:45.1421524`。`L2-RD-07` 首次在装置上跑：`17:36:59.727` 读到界面「05:00」，按服务端期限应显示约 300 秒、按到站期限约 286 秒，差 14 秒，分得清是哪一个 |
| `real-onboard-compensate-then-reconnect-01` | PASS 42s | 只留 CI artifact，没入库 |
| `real-onboard-restart-while-waiting-operator-01` | PASS 68s | 只留 CI artifact，没入库 |
| `real-onboard-load-door-closed-empty-reopens-01` | FAIL（1）101s | 12 条只红 `L2-DC-08`：终结原因码为空（期望 `CANCELLED_BY_OPERATOR`）。原因是集成分支上就有的丢失更新，与本票无关，见 `../../l1/20260924-cs339-lost-update-probe/`，另开票修。「装货结果未回」那段分支每一轮都走（这个场景没有断线，所以不升版），其余 11 条全过 |

按调度决定，这一轮就是审查 M2（「装货结果未回」那条路在真车载端上跑过）的证据，不重跑。
