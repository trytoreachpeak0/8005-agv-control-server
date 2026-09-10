# `G3`（进程重启）：`FP-IS-14` 的拒绝路径 **`PASS`**

`STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT`，25 条断言全绿，
`FP-IS-00` / `FP-IS-06` / `FP-IS-14` / `FP-IS-15` 四份 `gate-result.json` 的 `formalSlicePass`
都是 `true`。被测服务端绑 `6dc4bc8f50472301027bb603d99b8c386bbe0c72`。

## 实读

| 时刻 | 发生了什么 |
| --- | --- |
| phase 1 之后 | 车与已批准那版一致，激活 `ACTIVATED`，version 2，`de93ca3d…`——**对照组** |
| phase 2 之前 | 篡改车上**生效配置文档**的 `PulseResetMilliseconds` 500 → 600，文档哈希 `d543e59b…` → `fc5bfd31…` |
| phase 2 之后 | 车**连上了**（会话代恰好 +1 到 2），就绪 `RecoveryRequired`，原因码 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH` |
| 第二次激活 | 命令送达，车报 `{"Succeeded":false,"ReasonCode":"SLOT_CONFIGURATION_FINGERPRINT_MISMATCH"}`，激活行 `FAILED` |
| 生效配置 | 仍是 version 2、`activationId 01a686dc…`，**一个字段没动** |
| phase 3（服务端重启） | 会话代 3，原因码仍是 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH` |

三个阶段的会话代是 1 / 2 / 3，没有任何重连循环。

## 三条新断言

| 断言 | 结果 |
| --- | --- |
| `slotConfigurationActivationAcceptedWhileTheVehicleMatched` | `PASS` |
| `slotConfigurationActivationRefusedAfterTheVehicleConfigurationChanged` | `PASS` |
| `aRefusedActivationLeftTheActiveConfigurationUntouched` | `PASS` |

对照组不可省：没有先被接受的那一次，后面的拒绝只能说明「有什么东西坏了」，说明不了「是指纹比对拒绝
的」。第二条要求 `ReasonCode` 就是那个稳定错误码，别的失败也会产生 `FAILED` 行。第三条是运维上最要紧
的：服务端要是因为一次拒绝挪动了自己认定的生效版本，之后每一次比对都会拿错的期望值，那台车会永远被
拒，原因没人看得见。

## 走到这一份之前的三次失败

这条路径的 G3 跑了四次才绿，前三次全部原样保留，每一次都揭出一件真事：

1. **`../20260910-fp-is-14-fingerprint-mismatch/`**——改的是 `appsettings.json`，两次激活都被接受、
   指纹一字未变。**车上的生效配置是持久化文档**，`appsettings` 只在文档不存在时生成一次初始值。篡改点
   改为那份文档。
2. **`../20260910-fp-is-14-fingerprint-mismatch-corrected/`**——篡改生效，但车卡在握手：
   `CapabilitySnapshot` 的指纹不符时服务端回 `ProtocolProblem`、会话不建立。**不一致本身堵死了修复
   不一致的那条路**，车永远上不了线，也就收不到能修它的激活。同时暴露车离线时下发激活回 500。
   用户定了口径：降为不就绪、会话照建；离线下发回 202。
3. **`../20260910-fp-is-14-fingerprint-mismatch-unready/`**——降为不就绪之后车仍然连不上，会话代从 2
   涨到 16。服务端日志：`Session reason code 'SLOT_CONFIGURATION_FINGERPRINT_MISMATCH' has no
   protocol ErrorCode mapping.`——就绪结果发给车之前要过一张映射表，表里没有它。单元测试直接调
   `DecideReadinessAsync`，没走序列化那一步，所以没看见。

另有一处不是 G3 跑出来的，是第 2、3 次之间自查发现的：新会话没有清空上报指纹，而且「车还没报」会被
误报成指纹不符。跑 G3 之前就修掉了，提交 `6b75141`，没有对应的失败目录。

## 装置

车载端与模拟器从本地仓克隆（`-OnboardRepository`／`-SimulatorRepository`），`w2g/b3-on-v2` 已推送，
`afba86e0` 就在 `origin/w2g/b3-on-v2` 上。commit 绑定从 `scripts/run-staged-g3.ps1` 的 param 块读回，
`commitBindingSharedWithMainRunner` 在断言这件事。

## 未在本轮证明的

- **车上告警内容仍然是空的。**`OnboardAlarmBoard.Raise` 在车载端产品代码里没有调用者，#28 没有接上
  任何告警来源。这不是断言能补的，要先决定什么条件算一条告警。
- **这一次篡改的车没有被修回来。**断言证的是「拒绝、不动生效版本、车仍在线」；「下发一次与车上一致
  的版本让它重新就绪」这条恢复链路没有跑——在 `ApprovedSlotHardwareFacts` 写死的前提下，服务端发不出
  一个 600 ms 的版本。
- `protocol-v1.0.0` 这个 tag **尚未打**。本次绑的是 commit。
