# 旅程终结原因被引擎的旧读数覆盖（丢失更新）：确定性探针

**只作证据，不进测试工程。**`LostUpdateProbeTests.cs` 放在这里不会被编译；要重跑时把它拷进 `tests/ControlServer.Tests/`，
跑 `--filter FullyQualifiedName~LostUpdateProbeTests`，跑完删掉。这个缺陷另开票修（control-server#357），本票（control-server#339）不修。

**来由。**CI 真装置 run `35896134304` 里 `real-onboard-load-door-closed-empty-reopens` 只红 `L2-DC-08`：取消收尾之后需求 `Cancelled`、
旅程 `Completed`，终结原因码却是空的（`../../l2/20260924-cs339-ci-35896134304/`）。服务端日志（同目录 `control-server-lost-update-excerpt.log`）：
入站那一路在自己的事务里收尾旅程，`UPDATE "JourneyRuntimes" SET "BlockReasonCode" = @p0 (Size = 21) …, "Stage" = …`；随后引擎那一轮的
`UPDATE "JourneyRuntimes" SET "BlockReasonCode" = @p0, "BlockReasonSince" = @p1, "UpdatedAt" = @p2 WHERE "JourneyId" = @p3` 等了 797 ms 写锁后执行，
`@p0` 没有长度，也就是 null。

**机理（读代码与日志）。**引擎那一轮在入站事务提交之前读了旅程行（`AwaitingLoadResult`，码 `STATION_TIMEOUT_DOOR_NOT_CLOSED`）；门一关，
`ReconcileStationTimeoutDoorNotClosed` 在内存里把码清空，保存时把刚写进去的 `CANCELLED_BY_OPERATOR` 盖成空。`JourneyRuntimeRow` 没有并发令牌，
`WHERE` 里只有 `JourneyId`。关门这一个动作同时带来取消结果与「门已关」的安全事实，两个写入者正好撞在一起。

**探针。**到期限后门开着，让码变成 `STATION_TIMEOUT_DOOR_NOT_CLOSED`；证明门已关；在引擎那一次保存开始之前（`SaveChangesCounter.FailWhen`
的回调里，回调返回 false 不注入失败）用同一条连接把旅程写成 `Completed` + `CANCELLED_BY_OPERATOR`；让引擎继续保存。

| 提交 | 结果 |
| --- | --- |
| `012c31b2`（集成分支基点） | `stage Completed, code (null)`，断言期望 `CANCELLED_BY_OPERATOR`、实际 null（`probe-at-012c31b2.txt`） |
| `210ac78a`（本票修复之后，产品代码同 `d742f0ae`） | 同样的结果（当时读到的输出，没有单独存档） |

所以这个丢失更新在基点上就有（读到的）。本票在「装货结果未回」那段分支里，在清码与保存之间多了一次按 id 查发件箱
（`AdvanceWorklistPastAStaleDeadlineAsync`），窗口的起点是这一轮开头读旅程行，这次查询让窗口略宽（推的，没量）。
这个场景此前仓里 8 份证据的 `L2-DC-08` 全是 PASS，这是第一次红。
