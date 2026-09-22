# control-server#316 证据：在途单挂起、状态不明、被人工取消

票：control-server#316（#299 拆出的 T1，上真车前）。

**本票中途改过一次范围。**第一版（`8a1a840b`，证据 `green-3cbae94d/`）在人工取消在途单后会释放改派。用户 2026-09-22 更正：RIoT 里取消多半是误操作，该做的是重建订单，不是改派；怎么重建另行决定。在那之前，取消后的行为改成「写码、告警、不接追加，不释放、不改派、不建单」（`a744448a` 测试先红，`fd41aa9e` 实现）。第一版的证据留在原处，没有删，但它证明的是一个已经撤回的行为。

## L1

| 文件 | 内容 |
| --- | --- |
| `l1/red-before-fix-runtime.txt` | 测试提交 `1ba63371`（fp/v2-impl@8ec088b1 加新用例、不含修复）上 13 条红，都是「码为空」「追加进去了」「没有释放」这一类，不是编译或夹具错误。其中释放的几条属于第一版，已被下一行取代 |
| `l1/red-before-fix-dashboard.txt` | 测试提交 `05a197e0` 上看板说明 3 条红：`... has no description` |
| `l1/red-hold-instead-of-release.txt` | 测试提交 `a744448a` 在第一版实现上 3 条红：释放服务把取消当成释放理由（`Trigger = ORDER_ENDED_WITHOUT_ARRIVAL, Result = RELEASED`） |
| `l1/red-unreadable-order-clears-hang.txt` | 测试提交 `7687ffca` 上 1 条红（独立审查第 1 条）：挂起期间一次读不到订单，码被清成空 |
| `l1/pin-busy-reverse-verification.txt` | 钉住「被取消的旅程这辆车不进空闲候选」的用例：让等人的旅程不算 busy 时它红（`DoesNotContain` 命中 ELIGIBLE 积压）；两行 DIAG 是改法下与原样下那条需求的积压原因，说明派车租约是第二道 |
| `l1/reverse-verification.py`、`.log` | 在第一版修复之上逐项退回、各跑一遍相关用例。M4、M5 针对的释放触发已经撤回，其余各项仍适用于现在的代码 |

## 合成 L2（本机，经 `Invoke-HeavyLocal.ps1 -Ticket cs#316`）

每次运行只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，日志与快照没有入库。

| 目录 | 代码 | 结论 |
| --- | --- | --- |
| `red-base-8ec088b1/in-transit-order-hang-continue-001` | fp/v2-impl@8ec088b1，场景脚本从 scratchpad 副本运行 | FAIL：60 秒等不到 `ORDER_HANG`，阻断码为空 |
| `red-base-8ec088b1/in-transit-order-cancelled-held-001` | 同上 | FAIL：60 秒等不到 `ORDER_ENDED_WITHOUT_ARRIVAL`，阻断码为空 |
| `red-base-8ec088b1/in-transit-order-cancelled-redispatch-001` | 同上 | FAIL（第一版场景，已被上一行取代） |
| `green-fd41aa9e/`（7 条） | 现在的实现 `fd41aa9e` | 两条新场景 PASS；故障与急停 `command-surface-order-hold`（见下）、`emergency-stop-single-trigger`、`emergency-stop-operator-release` PASS；失联码 `onboard-silent-liveness-loss`、追加 `multi-stop-append-same-zone` PASS |
| `green-c87196e5/`（3 条） | 最终实现 `c87196e5`（只比 `fd41aa9e` 多了注释、看板文案与一条钉构造的用例） | 两条新场景与 `command-surface-order-hold` PASS |
| `green-3cbae94d/`（8 条） | 第一版 `3cbae94d` | 全部 PASS，含已撤回的 `in-transit-order-cancelled-redispatch` |

`green-fd41aa9e/command-surface-order-hold-001` 是红的，留着：三台车一辆都没派出去，积压原因是 `ONBOARD_FACTS_NOT_READY`，三条会话都是 Ready。根据机理可以排除本票：`3cbae94d`→`fd41aa9e` 之间 src 只改了三处——引擎在途分支（需要先有旅程才走得到）、看板文案，以及把释放服务改回 fp/v2-impl 原样。这一轮一趟旅程都没有，这三处一行都没执行。同一棵树立即重跑，`-002` PASS。

修前红证据取自 scratchpad 里的场景副本，与本分支入库的脚本逐字节相同（`git hash-object`：`in-transit-order-cancelled-held.ps1` `1d6d688f`，`in-transit-order-hang-continue.ps1` `66777f86`）。
