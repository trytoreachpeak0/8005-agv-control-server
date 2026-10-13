# cs#653 本机真装置实跑证据

2026-10-13 15:07～15:20（北京时间），控制端笔记本，调度 C10 放行的本机真装置时段，取桌面锁 `Global\W2G-InteractiveDesktop`。

| 端 | 提交 |
| --- | --- |
| control-server | `146d560d58c7a7b9f9fa24bd44bcca58b58a0cd8`（本 PR，已合入当时的 `fp/v2-impl`） |
| onboard-hmi | `91766e383625baa1f56ba10f803bfc25fac9822b`（`w2g/fp-v2-impl` 顶端，detached worktree） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main` 顶端，detached worktree） |

对照脚本：8005-workspace `b6c7a67` 的 `remote-ops/onboard-hmi/scripts/18-read-hmi-screen-state.ps1`（`-Local -CrossCheck`），由 `Watch-L2ScreenStatePauses.ps1` 每个停顿点调用 10 次。驱动脚本 `run-cs653-rig.ps1`，汇总 `results.json`。

## 结论

| 遍 | 场景 | 开关 | 结论 | 用时 |
| --- | --- | --- | --- | --- |
| 01 | `real-onboard-compensate-then-reconnect` | `-ScreenState -ScreenStatePauseSeconds 300` + watcher | PASS | 5.1 分钟 |
| 02 | 同上 | 不带 | PASS | 0.8 分钟 |
| 03 | `real-onboard-restart-with-open-recovery-session` | `-ScreenState -ScreenStatePauseSeconds 300` + watcher | PASS | 5.9 分钟 |
| 04 | 同上 | 不带 | PASS | 0.8 分钟 |

## 验收点（审查 S1、S2 与调度清单）

| 验收点 | 结果 |
| --- | --- |
| 每个停顿点跑满 10 轮 | 7 个点全部 `roundsRun = 10`、`incomplete = false` |
| 没有 `window-elapsed` | 7 次停顿全部 `released`，最长 90.6 秒（窗口 300 秒） |
| 没有 `unreached` | 两遍都为空 |
| 每次停顿期间的 IO 离线次数 | 7 次停顿全部 0；两遍整轮也都是 0 |
| 带开关与不带开关的判定一致 | compensate 9 条判据、restart 8 条判据，逐条 id 与结论完全相同 |

每个停顿点的对照结论：

| 遍 | 停顿点 | 用时 | 对照 10 轮 |
| --- | --- | --- | --- |
| 01 | `unknown-load=UNKNOWN / RecoveryRequired / Blocked` | 84.9 秒 | MATCH 8、MOVING 2 |
| 01 | `onboard-compensation-entry=True` | 89.1 秒 | MATCH 10 |
| 01 | `@dialog=补偿清空` | 74.6 秒 | MATCH 10 |
| 03 | `onboard-resume-entry-before-restart=True` | 90.6 秒 | MATCH 9、MOVING 1 |
| 03 | `@dialog=申请恢复原操作` | 70.2 秒 | MATCH 10 |
| 03 | `onboard-compensation-entry-after-restart=True` | 63.7 秒 | MATCH 10 |
| 03 | `@dialog=补偿清空` | 84.0 秒 | MATCH 10 |

没有一轮 MISMATCH。三轮 MOVING 都落在各遍的第一个停顿里，读取脚本给出的原因是「UIA 读屏前后 `contentRevision` 变了」（2→3、3→4、2→3）：这两个停顿点都紧跟在车载端重启之后（01 遍车载端 15:08:06 重新连上 IO，15:08:08 进停顿），屏幕还在陆续刷新。读取脚本在这种情况下按设计不下结论，不是快照与屏幕不一致。

## 其他核对

- **IO 离线计数读的位置是对的。** 两遍的 `logs/onboard-app/agv-20261013.log` 里各有 2～3 行「IO模块在线」，没有「IO模块离线」；计数 0 是读到了日志、日志里没有，不是没读到。
- **快照与时间线落进了证据。** 两遍的 `screen-state/` 下都有 `hmi-state.json` 与 `hmi-state-20261013.ndjson`。
- **关着时证据布局不变。** 02、04 两遍没有 `screen-state*` 目录，`assertions.json` 身份字段里没有 `screenState`。
- **台账。** `%LOCALAPPDATA%\8005-l2\rig-runs.log` 里 01、03 两遍的开始与结束行带 `screenState: true`，结束行带 `pauseSecondsTotal`（248.6、308.5）；02、04 两遍没有这两个字段。

## 精简说明

为控制仓库体积，入库的是精简版：

- 每轮都保留对照结论文件 `*-crosscheck.json`（含逐项比较）。
- 读屏前后两份快照与 UIA 读数，只保留每个停顿的第 1 轮和三轮 MOVING。
- 截图一律不入库。
- 每遍保留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`、车载端日志、快照目录、运行日志与 watcher 日志；服务端与模拟器的组件日志、收尾数据库快照不入库。

完整原始证据留在本机 `F:/8005-scratch/cs653-rig/`。

## 还没有覆盖的

- hmi#314 还没合入，快照里与输入相关的字段不完整；它合入后，这些字段的对照要重跑。
- 车上的两项（不带 `-CrossCheck` 的纯读取模式与 IO 离线、`agvops` 读快照的权限）台架证明不了，留给 hmi#316 上车。
