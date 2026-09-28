# control-server#335 最终 head 验收：CI 真装置 run 36425683659

两路审查的必修改完之后，在最终 head 上补跑 `real-onboard-normal-load` 与 `real-onboard-in-transit-door-facts` 各 1 遍。持有由
Coordinator 8 于 2026-09-28 放行，三端提交由调度读远端核过。

## 四行核对

三行提交取自场景那一步（`Run real-onboard L2 scenarios`）：

```
control-server @ f524b16a2f2b6c5bd819ad7f03395a9682a86024
8005-agv-onboard-hmi @ 60f341878a1bce705598aa9043234c1ca2fb1d7a
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
real-onboard-normal-load-01 -- PASS, 76s；real-onboard-in-transit-door-facts-01 -- PASS, 111s
```

停止条件 `RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 命中 1 行，是源码回显（带字面 `^[[36;1m`），去掉回显行后命中 0。
artifact `real-rig-evidence` 522612 字节，先核非空再做以上核对。

## 与上一轮（run 36404884491）的区别

场景脚本按审查建议改过：行驶时 `currentPosition = 0`（车在两站之间），DF-06、DF-08 用 `Wait-L2Iterations` 按轮数等，不再固定睡眠。
所以 DF-08「解除之后不再急停」这一轮是在车报不出站点时断的，才真正看得见豁免那条规则。结果：`real-onboard-normal-load` 12/12，
`real-onboard-in-transit-door-facts` 8/8，全部 PASS。

本目录只留两个场景的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 与顶层 `SUMMARY.md`、`commits.json`；`logs/` 与
`snapshots/` 在 run 的 artifact 里。PASS 不代表真实 RCS、真车、真实 IO 模块或接线合格。
