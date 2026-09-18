# cs#151 staged G3：判据退回旧版本（红证据）

- 结论：`STAGED_SLICE_FAIL`；只有 `FP-IS-07` `FAIL`，红的正是那三条：
  `forcedRecoveryGenerationAdvancesMonotonically`、`supersededGenerationResultIsHistoricalEvidenceOnly`、`recoveryNeverReportsFalseCompletion`，均为 `FAIL_OR_INCONCLUSIVE`。其余四个切片 `PASS`。
- 绑定：与新判据那次相同（`06b65688`：`e0f26b37`／`9748c418`／`fb5f7c59`／`86575456`）。
- harness：`93b09ebb` = `166acd80` 上只把三条判据换回 `06b65688` 的原文（数据库观测新增项保留），临时副本专用，未推送。
- 用途：证明新判据不是把断言删弱到恒真——同一产品行为下，旧判据仍红、新判据变绿。与 cs#90 出口那次红（`b5-36/batch-5-exit` 上 `evidence/g3/20260918-protocol-v2.0.0-staged-06b65688`）一致。
