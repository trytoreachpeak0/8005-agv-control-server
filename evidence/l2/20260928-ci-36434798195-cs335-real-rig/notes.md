# control-server#335 最终 head 验收：CI 真装置 run 36434798195

增量复核的两条必修（会话未就绪一侧的门锁故障监看、网关单态用例）改完之后，在最终 head 上补跑
`real-onboard-normal-load` 与 `real-onboard-in-transit-door-facts` 各 1 遍。持有由 Coordinator 8 于 2026-09-28 放行，
三端提交由调度读远端核过。

## 四行核对

三行提交取自场景那一步（`Run real-onboard L2 scenarios`）：

```
control-server @ 58144df586e98f78947c2fd2679a54726330624c
8005-agv-onboard-hmi @ 60f341878a1bce705598aa9043234c1ca2fb1d7a
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
real-onboard-normal-load-01 -- PASS, 72s；real-onboard-in-transit-door-facts-01 -- PASS, 113s
```

停止条件 `RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 命中 1 行，是源码回显（带字面 `^[[36;1m`），去掉回显行后命中 0。
artifact `real-rig-evidence` 554258 字节，先核非空再做以上核对。结果：12/12、8/8，全部 PASS。

本目录只留两个场景的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 与顶层 `SUMMARY.md`、`commits.json`；`logs/` 与
`snapshots/` 在 run 的 artifact 里。PASS 不代表真实 RCS、真车、真实 IO 模块或接线合格。
