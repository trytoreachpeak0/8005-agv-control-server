# cs#742 合成 L2 证据

本机时段第 2 轮（段二，10-17 18:03 放行），每个场景绿、红各一轮，经 `Invoke-HeavyLocal -Ticket cs#742`，没有重试。只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，日志与快照不入库。

| 场景 | 轮 | 改动 | 结论 |
| --- | --- | --- | --- |
| `load-cancelled-journey-continues-departs-early` | 绿 | 无（head `b9d747e4` 之前的 `bb69420e`） | PASS 6/6。取消结果确认 10:06:17.143，离站核验 10:06:17.763（约 0.6 秒），期限 10:07:46.7；12 号站甲装上后等满 90 秒才走 |
| 同上 | 红 | 本地临时提交，运行期假条件关掉 #742 新分支 | FAIL：CDE-04 核验 10:11:00.64，期限 10:10:59.64，按期限才走。CDE-05 两边都成立（车等到期限才走时屏上期限本就不晚于离站），区分由 L1 承担 |
| `machine-departure-stale-worklist-after-cancel` | 绿 | 无（`b9d747e4`） | PASS 4/4。第 5 版取消后空清单被只补期限的第 6 版顶掉，带第 5 版点出发答 ACCEPTED，核验早于期限 |
| 同上 | 红 | 本地临时提交，运行期假条件关掉 `StaleWorklistWithoutSubstantiveChange` | FAIL：MDS-03 答 REJECTED `WORKLIST_REVISION_STALE`（与现场 agv03 140 站同形），MDS-04 没发核验 |
