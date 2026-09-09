# `CONTROL_SERVER_G2`：`FP-IS-00`～`07` 在协议 v2 下重证（`a143c9c`）

票 17 的三分之一。**这是 v2 下的重新证明，不是沿用任何既有结论。**
`protocol-v0.1.1` 时代那八份 `W2G-IS-NN` 证据绑的是服务端已经不再发送的 manifest 哈希，
按 `ConformanceRunIdentity` 它们对 v2 不成立；`RELEASE-CANDIDATE.md` 第 12 节末也把
`W2G-IS-00`～`07` 与 RC 记为 `INCONCLUSIVE`。本目录不引用、不继承、不折算那些结论。

## 运行类型

纯 tier 1 / G2。**不动车、不建单、不使用任何现场凭据、未触碰已安装服务。**
2026-09-09 在 `LAB-WIN-01` 上运行，`.NET SDK 8.0.425`（`global.json` 钉死，`rollForward: disable`）。

## 结论

| 项 | 结果 |
| --- | --- |
| 全量测试（Release，`ControlServer.Tests`） | `586 passed / 0 failed / 0 skipped` |
| 八片 `CONTROL_SERVER_G2` | **八份 `gate-result.json` 全部 `PASS`** |
| 绑定 | `a143c9cb1f79dacf6f23a4992a15e7e680ef21d3` ＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |

八份逐片：

| 片 | `selectedTestCount` | `testExitCode` | 向量 |
| --- | --- | --- | --- |
| `FP-IS-00` | 59 | 0 | `CV-SESSION-RECOVERY-HAPPY`、`CV-SESSION-RECONNECT-DURING-RECOVERY`、`CV-SNAPSHOT-REPLACE-AND-ACK`、`CV-SNAPSHOT-SAME-REVISION-CONFLICT` |
| `FP-IS-01` | 69 | 0 | `CV-DEMAND-ACCEPT-TO-PICKUP` |
| `FP-IS-02` | 17 | 0 | `CV-PICKUP-SUBLOT-LOAD`、`CV-LOAD-CORRECTION`、`CV-LOAD-CANCELLATION-ALL-EMPTY` |
| `FP-IS-03` | 27 | 0 | `CV-PREDEPARTURE-SAFETY-EXPIRES`、`CV-OPERATION-RESULT-UNKNOWN-RECONCILE` |
| `FP-IS-04` | 10 | 0 | `CV-DESTINATION-UNLOAD-ALL-EMPTY` |
| `FP-IS-05` | 12 | 0 | `CV-CONNECTION-LOSS-SAFE-FINISH`、`CV-SESSION-RECONNECT-DURING-RECOVERY` |
| `FP-IS-06` | 38 | 0 | `CV-RELIABLE-RETRY-SAME-CONTENT`、`CV-RELIABLE-RETRY-DIFFERENT-CONTENT`、`CV-REQUEST-FIRST-RESULT-REPLAY` |
| `FP-IS-07` | 19 | 0 | `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`、`CV-EXCEPTION-RESUME`、`CV-EXCEPTION-COMPENSATE`、`CV-FAULT-CARGO-HANDOFF`、`CV-FORCED-MECHANICAL-RECOVERY`、`CV-MANUAL-CHARGING-RETURN` |

合计选中 251 条（含跨片重复：`CV-SESSION-RECONNECT-DURING-RECOVERY` 与
`CV-OPERATION-RESULT-UNKNOWN-RECONCILE` 各属两片）。

## 身份绑定，八份逐字段一致

`schemaVersion` `1.1.0`；`protocolReleaseVersion` `1.0.0`；`protocolProfileId` `AGV_FULL_PRODUCT`；
`protocolVersion` `2`；`protocolTag` `protocol-v1.0.0`；`protocolRepositoryCommit`
`f6ee75defe6e2d18f63f4082bee445dbb678ab1b`；`protocolManifestSha256`
`84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788`；`protocolSchemaBundleSha256`
`71146c881e8ec199e9a977779ec1a557bed96a9ab71e36cfc3dfb7b329351c6b`；`protocolVectorsSha256`
`51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df`；`integrationSliceIndexSha256`
`71e0a63d49d1973653e1f70addc19c334faff5e53e8597733c1423a7307bd82f`。

**`integrationSliceIndexSha256` 与车载端同一轮 `ONBOARD_HMI_G2` 的那份逐字节相同**——两端读的
是同一张切片表，不是各自一份手抄。

## ⚠️ `protocolApprovalStatus` 是 `SUPERSEDING_CANDIDATE`

不是 `APPROVED_RELEASE`。`protocol-v1.0.0` 这个 tag 在协议仓里**至今没有打出**（规格 6.6 第 6 条
要两名不同产品负责人的 attestation ＋ 注释 tag，两件都还没发生），所以八份证据绑的是候选
commit 本身。`New-WireToGateReleaseCandidate.ps1` 会因此拒绝打 RC——**这是规格要的效果，
不是本轮要绕开的障碍**。本目录不构成、也不支持任何发布主张。

## 这份证据不说的话

- **不说 `ONBOARD_HMI_G2`。** 车载端同一轮的八份在
  `8005-agv-onboard-hmi` 的 `evidence/g2/20260909-fp-is-00-07-v2-recertification/`，
  绑 `w2g/fp-v2-impl@360a4059c9daa44a90f6b504cb25366d264b532a`。
- **不说 `G3`。** 联合 G3 本轮**没有运行**：`run-staged-g3.ps1` 从 GitHub 克隆车载端并断言远端
  tip 等于绑定 commit，而车载端本线的提交按用户 2026-09-09 的裁定尚未推送。八个切片因此**尚未
  通过**——一片要四道门禁齐全才算过。
- **不说真实硬件。** 车辆停稳信号、Modbus／锁／门／光幕、现场明文网络都不在本目录的主张范围内。

## 复现

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
foreach ($n in '00','01','02','03','04','05','06','07') {
    .\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice "FP-IS-$n" `
        -ProtocolManifest ..\8005-agv-protocol\manifest\release.json `
        -Output "evidence\g2\<新目录>\FP-IS-$n"
}
```

`-Output` 必须是不存在的目录；脚本在建目录之前先数选中的测试，选中 0 条即拒绝。
运行时协议仓在 `fp/v2-candidate`＝`f6ee75d`，工作树干净。
