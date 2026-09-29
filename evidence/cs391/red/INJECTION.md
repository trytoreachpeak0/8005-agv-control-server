# 红证据的注入

`l2-criterion-and-acceptance-reservation-off` 是在 `9cb83eae` 的工作树上叠了下面两处注入后跑的（SUMMARY 里的
`controlServerCommit` 只记 HEAD，不含工作树改动），跑完即用备份还原：

1. `FixedStationSingleOccupancyCriterion.EvaluateAsync` 开头直接返回 `ELIGIBLE`——关掉本票的判据；
2. `WireToGateStore.StageAndCommitAcceptanceAsync` 的受理预占条件加 `&& journey.MapId < 0`——关掉受理预占。

结果：关卡段 L2-FSO-01～04 仍 PASS（补预占与离点释放没被关），派工待送站段 L2-FSO-05～07 FAIL——第二条同站需求在
H 仍占着 305 时（`13:36:13.728`）就被另一台车接走。
