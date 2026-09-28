# control-server#335 验收：CI 真装置 run 36404884491

`l2.yml` 的 `real-rig` 作业（`-f rig=real`），跑 `real-onboard-normal-load` 与 `real-onboard-in-transit-door-facts`（带判据的正式版）各 1 遍。
持有由 Coordinator 8 于 2026-09-28 放行，服务端提交由调度读远端核过。

## 四行核对

三行提交取自场景那一步（`Run real-onboard L2 scenarios`），不是 checkout 那一步：

```
control-server @ 6f7a091afdcf53a18a9832ae43e8b9a56fd8f290
8005-agv-onboard-hmi @ 60f341878a1bce705598aa9043234c1ca2fb1d7a
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
real-onboard-normal-load-01 -- PASS, 76s；real-onboard-in-transit-door-facts-01 -- PASS, 112s
```

停止条件 `RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 在作业日志里命中 1 行，是源码回显（带字面 `^[[36;1m`），
不带回显标记的命中为 0。artifact `real-rig-evidence` 512515 字节，下载核过后再做以上核对。

## 结论

- `real-onboard-normal-load`：12/12 PASS。基线场景，证明本票的门锁检查没有让正常装卸与行驶停下。
- `real-onboard-in-transit-door-facts`：8/8 PASS。真车载端 WPF 与真 slots-simulator 上：
  - 空车行驶 24 个样本、有货行驶并途中重连一次 23 个样本，都不 Hold、不急停、不 Cancel；
  - 锁反馈变 0 后，Hold 恰好一条，打在本车关卡段那张单上；随后急停恰好一条，在 Hold 之后（09:44:00.879 Hold，09:44:01.270 急停）；
  - 故障事实记 `VEHICLE_DOOR_NOT_PROVEN_LOCKED`；锁反馈仍是 0 时不解除；
  - 锁反馈恢复后自动解除一次，原因 `EMERGENCY_DOOR_CAUSE_REMOVED`；解除之后单仍停着，没有 CONTINUE，没有第二次急停，没有 Cancel。

本目录只留两个场景的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 与顶层 `SUMMARY.md`、`commits.json`；
`logs/` 与 `snapshots/` 在 run 的 artifact 里。PASS 不代表真实 RCS、真车、真实 IO 模块或接线合格。
