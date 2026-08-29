# staged G3 重绑当前双端：`3d8b00c` + `304e6ad`

## 运行类型

`STAGED_G3_REAL_PEERS_DETERMINISTIC_TLS`。loopback 隔离，**不动车、不建单、不使用任何现场凭据**。
用户明确授权安装唯一临时测试根（`CurrentUser/Root`）。

## 结论

七条断言全部 PASS，绑定当前双端 commit：

| 断言 | 结果 |
| --- | --- |
| `identityRejections` | PASS |
| `sameConnectionSameMessageIdSameContent` | PASS |
| `sameMessageIdDifferentContentStableConflict` | PASS |
| `recoveryStateReportFirstAckDropReplay` | PASS |
| `recoveryStateReportFirstAckDropReplayOverTls` | PASS |
| `noMovementOrExternalSideEffects` | PASS |
| `secretScan` | PASS |

`status = STAGED_G3_TLS_RECOVERY_REPLAY_PASS`，`formalSlicePass = false`。
运行 `20260829T145856`，耗时 1 分 55 秒，`configurationSha256`
`3c351b1b1ecbbc6be9bee233c730171b2b0509039b82c9a3863d3935b425fc14`。

## 绑定身份

| 组件 | commit |
| --- | --- |
| ControlServer | `3d8b00c7558ae700358f1f995a5ac75d12a3250c` |
| OnboardHmi | `304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6` |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| protocol | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`（`protocol-v0.1.1`，G1 `PASS`）|

manifest `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`，
ControlServer `/version` 回读 `approvalStatus = APPROVED_RELEASE`。

## 本次为什么要重绑

此前 runner 默认绑 `ControlServer ea3050d` + `Onboard 15c6387`。两者都已过期：服务端此后经过五处
现场修复到 `3d8b00c`，车载端是王昆的 `304e6ad`，**而 `304e6ad` 改的正是快照 revision 去重键**
——重放探针打的就是这块。绑在无人再运行的一对上的证据，无法支撑票据 10 的剩余向量。

**风险已排除**：车载端换代后，恢复重放探针依然 PASS。第 1 代的首个 `DurableAck` 被丢弃并断开
TLS，第 2 代在新连接上以同一 messageId (`a7037f28-681f-46a2-96ba-520f2331911c`) 与同一 payload
SHA-256 重放并成功收到 Ack；inbox 中该 messageId 仅 1 行，`contentHash`
`021e7cd89b0c6475fed5b83120dffd4c70dfb8ce7533540b73f6d2f15590f8ea`，最终稳定到
`RecoveryRequired / CAPABILITY_SNAPSHOT_REQUIRED`。

## runner 的三处修复（本轮同时落地）

runner 在这台机器上原本**无法启动**，缺口都不是配置问题：

1. **`pnpm` 不可解析**（`a4de76b`）——本机 PATH 中没有 node/npm/pnpm。node 原有 fallback，pnpm
   没有，但以普通包形式与其同目录，可用 `node pnpm.cjs` 调用。
2. **精确克隆缺依赖**（`a4de76b`）——干净克隆没有 `node_modules`，而 `g1-validate.mjs` import
   `ajv`，G1 步骤即使有 pnpm 也会失败。现已在 G1 前加 `install --frozen-lockfile`。
   改动前已在一次性克隆上验证：install exit 0、G1 `PASS`、54 消息类型、1341 反例、0 失败。
3. **结果文档中的陈旧身份**（`be91169`）——`commits` 段原本除绑定 commit 外，还硬编码
   `onboardRunnerBinding = 4d8629c`、`onboardProductFix = 0584322`。两者是 `15c6387` 时代的
   8-26 commit，不随 `-OnboardCommit` 变化，导致本轮首次运行同时报出三个互相矛盾的车载端身份。
   已删除——`304e6ad` 就是车载端实现，不是叠在别的基线上的补丁。本文档对应的是**删除后重跑**
   的结果，`commits` 段只含四个真实绑定。

## 零副作用与清理

| 项 | 值 |
| --- | --- |
| `realRiotOrderCreated` | `false` |
| `movementCommandSent` | `false` |
| `realExternalCredentialsUsed` | `false` |
| `vehicleSafetyEligibilityFabricated` | `false` |
| `orderIntentCount` / `acceptedDemandCount` / `stationOperationCount` | 0 / 0 / 0 |
| `temporaryTrustCleanupVerified` | `true` |
| `secretLeakFiles` | `[]` |

运行后独立复核：`CurrentUser/Root` 中 `staged G3 loopback root` **0 个**，
58205／58207／58215／1502／58006 全部空闲，无 `SQCD*` 残留进程。

## 环境约束

`StageRoot` 必须是短路径。深目录会在克隆 protocol 仓时于最长的样例文件名上撞 Windows MAX_PATH，
报 `Filename too long` 并留下不完整的工作树。本次使用 `C:\w2g-sg3`。

## 明确未证明的事项

本轮把「重复」「异内容冲突」「恢复报告重放」三类抬到了当前双端 commit，但**不构成切片 G3**：

- `W2G-IS-00`、`W2G-IS-06` 均记录为 `INCONCLUSIVE`，`fullG3` 与 `releaseCandidate` 同为
  `INCONCLUSIVE`。
- drop／重放探针只覆盖 `RecoveryStateReport` 一种消息，未触及 `SlotOperationCommand`、
  `OperationResult`、`PreDepartureSafetyCheck`——票据 10 要求的「结果重放」正是后者。
- 「乱序／延迟」「断联安全收尾」「恢复分支」本轮未覆盖。
- 「进程崩溃重启」由另一独立脚本 `run-staged-g3-no-movement.ps1` 负责，该脚本仍绑
  `cc6e2b9` + `0455147`，且位于规划仓而非本仓。
