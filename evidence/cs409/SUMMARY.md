# cs#409 证据：清桩中的车自动开往等待点（批次9-11）

精简版：每份合成 L2 只留结论（`SUMMARY.md`）、判据（`assertions.json`）、时间线（`timeline.jsonl`）与服务端日志里相关的几行
（`control-server-excerpt.log`）；全量测试只留结尾；单测红证据只留失败的那几行。完整的日志与数据库快照没有入库（单份 3.6～10 MB）。

| 目录 | 是什么 | 结论 |
| --- | --- | --- |
| `l2/pass-56bc9491/` | 合成 L2 `charging-clearance-to-waiting-point`，审查修复后的提交 `56bc9491`（跑前护栏退出码 0） | PASS，6 条判据 |
| `l2/pass-9e02f41cc/` | 合成 L2 `charging-clearance-to-waiting-point`，提交 `9e02f41cc` | PASS，6 条判据 |
| `l2/red-fallback-to-any-station/` | 缺陷版本：无合格等待点时回退到登记外的站 12 | FAIL：L2-CWP-02 实际建了 30 张清桩意图；L2-CWP-03 随之红 |
| `l2/red-1-now-before-read/` | 第一轮真实的红：判「车静止在桩上」时先取此刻再读车，读数被判不新鲜 | FAIL：等待超时，`CHARGING_CLEARANCE_VEHICLE_OFF_CHARGER`；修于 `4f9d24347` |
| `l2/self-checks.txt` | 跑场景之前的 L2 自检（整数组返回护栏等 5 个） | 退出码都是 0 |
| `full/full-9194f1b03-tail.txt` | 本机全量，merge `origin/fp/v2-impl`（`6281bdab`，含 cs#410）之后的 `9194f1b03` | 4120 通过、0 失败 |
| `full/full-19ffa4cbf-tail.txt` | 本机全量，审查修复提交 `19ffa4cbf` | 4074 通过、0 失败 |
| `full/full-9e02f41cc-tail.txt` | 本机全量，提交 `9e02f41cc` | 4070 通过、0 失败 |
| `full/full-92585a2ee-tail.txt` | 本机全量，第一个提交 `92585a2ee` | 4068 通过、0 失败 |
| `l1-red/revert-hook.txt` | 撤掉清桩中那一支的接入点（当时测试类 20 条） | 14 条红 |
| `l1-red/split-save.txt` | 把到点完成拆成两次提交 | 崩溃点用例红：清桩已写完成而等待点仍是预占 |
| `l1-red/no-withdrawal-cooldown.txt` | 去掉撤回之后的冷却 | `AFlappingPremise…` 红 |
| `l1-red/now-before-read.txt` | 判定时刻放回读车之前 | `AReadingStampedAfter…` 红，与 L2 第一轮同一个症状 |
| `l1-red/withdrawal-m1.txt` | 建单前复核去掉「人工确认已记下就撤回」（审查 M-1） | `APremiseLostBetween…` 只红 manual-confirmation-recorded 一格 |
| `l1-red/withdrawal-s1.txt` | 建单前复核去掉「车不在桩上就撤回」（审查 S-1） | 只红 vehicle-moved-off-the-charger 一格 |
| `l1-red/withdrawal-s2.txt` | 建单前复核去掉「旧单不是已终结就撤回」（审查 S-2） | 只红 old-order-not-ended-any-more 一格 |
| `l1-red/severity-not-critical.txt` | 看板 `SeverityOf` 去掉 `CHARGING_CLEARANCE_TO_WAITING_POINT` 的 Critical（增量审查补测） | `EachClearanceMoveAlarmHasItsSeverity` 只红 Critical 那一格 |

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，不代表真车、真实 RCS 合格。
