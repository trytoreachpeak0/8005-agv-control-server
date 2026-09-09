# `CONTROL_SERVER_G2`：`FP-IS-02` 在补齐 `RECONCILE_EMPTY_FINAL_STATE` 覆盖之后重出（`3f62647`）

**这是 v2 下的重新证明，不是沿用任何既有结论。**本目录只补出 `FP-IS-02` 一片。
同一轮八片的那份证据在
[`../20260909-fp-is-00-07-v2-recertification/`](../20260909-fp-is-00-07-v2-recertification/)，
**保留不动**——那份绑 `a143c9c`，是当时代码的真实结论，不因本次重出而作废。

## 为什么重出

票 16 转交、票 17 承接的那条测试补上了：`CV-LOAD-CANCELLATION-ALL-EMPTY` 对服务端要
`AUTHORIZE_CANCELLATION_EXPLICITLY` 与 `RECONCILE_EMPTY_FINAL_STATE` 两件，此前只有前者被
`FailedCompensationResultIsDurableReplayableAndNeverReleasesDemandOrVehicle` 的 `REJECTED`
分支覆盖。`3f62647` 加了
`AuthorizedLoadCancellationReconcilesOnlyWhenEverySlotIsProvenEmpty`。

`ConformanceRunIdentity` 要求任一绑定分量变化都必须建立新运行，`implementationCommit`
由 `a143c9c` 变为 `3f62647`，所以这是一次新运行而不是对旧目录的修改。

## 运行类型

纯 tier 1 / G2。**不动车、不建单、不使用任何现场凭据、未触碰已安装服务。**
2026-09-09 在 `LAB-WIN-01` 上运行，`.NET SDK 8.0.425`（`global.json` 钉死，`rollForward: disable`）。

## 结论

| 项 | 结果 |
| --- | --- |
| 全量测试（`ControlServer.Tests`） | `587 passed / 0 failed / 0 skipped`，Debug 与 Release 各一遍 |
| `FP-IS-02` 的 `CONTROL_SERVER_G2` | `gate-result.json` `PASS`，`testExitCode` 0 |
| `selectedTestCount` | **18**（上一轮 17，新增的正是补上的那一条） |
| 绑定 | `3f626476ec6cb0eb309719c7edc71a18ff55bd31` ＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |

### trx 对账

`control-FP-IS-02.trx` 里 `<UnitTestResult` 共 **18** 条，与 `selectedTestCount` 相等，
18 条 `outcome="Passed"`、0 条其它。`AuthorizedLoadCancellationReconcilesOnlyWhenEverySlotIsProvenEmpty`
在 trx 中出现。**这一步是逐条数出来的，不是信 `--logger` 的文件名。**

## 身份绑定：除实现 commit 外与上一轮逐字段相同

`schemaVersion` `1.1.0`；`protocolReleaseVersion` `1.0.0`；`protocolProfileId` `AGV_FULL_PRODUCT`；
`protocolVersion` `2`；`protocolTag` `protocol-v1.0.0`；`protocolRepositoryCommit`
`f6ee75defe6e2d18f63f4082bee445dbb678ab1b`；`protocolManifestSha256`
`84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788`；`protocolSchemaBundleSha256`
`71146c881e8ec199e9a977779ec1a557bed96a9ab71e36cfc3dfb7b329351c6b`；`protocolVectorsSha256`
`51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df`；`integrationSliceIndexSha256`
`71e0a63d49d1973653e1f70addc19c334faff5e53e8597733c1423a7307bd82f`。

向量清单未变：`CV-PICKUP-SUBLOT-LOAD`、`CV-LOAD-CORRECTION`、`CV-LOAD-CANCELLATION-ALL-EMPTY`。

## ⚠️ `protocolApprovalStatus` 是 `SUPERSEDING_CANDIDATE`

与上一轮同：候选尚未获批，`New-WireToGateReleaseCandidate.ps1` 因此拒绝打 RC。这是规格 6.6
要的效果。**本目录不构成切片通过**——`FP-IS-02` 要四道门禁齐全（`G1`、`CONTROL_SERVER_G2`、
`ONBOARD_HMI_G2`、`G3`）才算过，见协议仓 `integration-slices/index.json`。

## 本次不重出的部分

`ONBOARD_HMI_G2` 未重跑：那条测试改的是控制端产品测试，车载端一个字节都没动，
其 `implementationCommit` 未变，既有那份仍然成立。
