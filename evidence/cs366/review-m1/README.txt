独立审查第一路 M1（2026-09-28）：ORDERING、CreateAttemptCount 0 的有货重建被挡一次后，宿主不再要快照。
- red-1b6770b7.txt：修前红。
- 修复 72e06343：ClaimCargoEvidenceRequestAsync 的到期查询与条件更新加上 ORDERING 且新意图从没发出。
- 修后受影响的类 386 条：385 过、1 红 MultiVehicleExecutionTests.AVehicleCutOffInTheMiddleOfTheChainDecidesAsBeforeTheMove
  （1 号车 1000 ms 派车预算耗尽，真实计时器，见记忆 budget-tests-real-timers）；同一份代码单独连跑 5 遍 5 过；这条走派车轮次，
  不经过 ClaimCargoEvidenceRequestAsync。判为负载下的偶发，不是本票。
- mutation-m7-claim-pending-only.txt：宿主条件退回只认 PENDING，恰好红这四格。
- probe-injection-paths.txt：进程停下与对账答 Unknown 两种注入的对照。
