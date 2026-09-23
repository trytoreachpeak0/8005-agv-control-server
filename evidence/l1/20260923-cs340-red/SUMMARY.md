# cs#340 红证据：握手未完成时答复后多出一行 SessionReadiness

每条新用例先单独提交，在那个测试提交上跑出失败，再提交实现。五份输出都红在同一条断言上：
补发那一条的答复应当只有一行，实际多了一行 `SessionReadiness`。

| 文件 | 测试提交 | 用例 | 那一处（`fp/v2-impl@18172346` 行号） | 现场到得了吗 |
| --- | --- | --- | --- | --- |
| `01-safety-change.txt` | `1e87f6c6` | `ASafetyChangeFirstDeliveredInTheReconnectHandshakeIsOnlyAcknowledged` | `:630`，`SafetyStateChanged`，无条件附就绪 | **到得了**，onboard-hmi#204 的现场就是这一条 |
| `02-operation-result.txt` | `a604ea34` | `AnOperationResultInTheReconnectHandshakeIsOnlyAcknowledgedEvenWhenItChangesReadiness` | `:526`，`OperationResult` 等，就绪变了才附 | 到不了（见下） |
| `03-recovery-result.txt` | `ea4a2921` | `ARecoveryResultFirstDeliveredInTheReconnectHandshakeIsOnlyAcknowledgedEvenWhenItChangesReadiness` | `:572`，五个恢复结果，就绪变了才附 | 到不了 |
| `04-duplicate-arrival.txt` | `cd050893` | `AMessageArrivingASecondTimeInTheReconnectHandshakeIsOnlyAcknowledgedEvenWhenItChangesReadiness` | `:790`，`RebindDurableAckAsync`，就绪变了才附 | 到不了 |
| `05-hardware-record.txt` | `fced7775` | `AHardwareRecoveryRecordInsideTheReconnectHandshakeIsAnsweredWithoutReadinessEvenWhenItChangesReadiness` | `:542`，`HardwareRecoveryRecordSubmitted`，就绪变了才附 | 到不了；而且它是 REQUEST，车载端不会在握手里补发 |

失败原文（01，两种发送方式都红）：

```
Assert.Equal() Failure: Collections differ
Expected: ["DurableAck"]
Actual:   ["DurableAck", "SessionReadiness"]
                         ↑ (pos 1)
```

## 为什么四处「到不了」，以及那四条用例怎么让它红

那四处只在「重新判定的就绪与连接上记的不同」时才附就绪。握手期间这件事发生不了：`SessionHello` 把连接置为
`RecoveryRequired`，同时清空会话行本代次的 `RecoveryReportId`，而判 `READY` 必须有它，所以恢复报告之前每次判定都是
`RecoveryRequired`，与连接状态相等，不附。车载端每次握手都用新 GUID 发 `SessionHello`，这条路没有例外。

所以 02～05 在 `SessionHello` 之后把连接状态手动置为 `READY`（现场到不了的前提，用例注释写明），让每一处自己的附带
路径被走到。用户 2026-09-23 在会话里选定这种取证方式（「构造前提加钉住前提」）。前提本身由
`InsideTheReconnectHandshakeReadinessCannotChangeBeforeTheRecoveryReport` 钉住，它的判别力见反向验证 P1。

## 06：全部新用例对着修前的处理器

`06-all-new-tests-on-base-processor-18172346.txt`：在 `af995c34` 上把 `OnboardMessageProcessor.cs` 换回
`18172346` 的版本（`git checkout 18172346 -- <file>`，跑完换回），跑全部新用例：

- 红：上面五条、`AVehicleNotReadyOnItsOwnOrderHandshakesWithoutAnExtraReadinessAndIsStillToldAfterwards`（它在握手里
  补发的也是 `SafetyStateChanged`）、护栏两条。
- 绿：`InsideTheReconnectHandshakeReadinessCannotChangeBeforeTheRecoveryReport`、
  `TheRecoveryReportAnnouncesTheReadinessAResendInsideTheHandshakeLeftBehind`、
  `AfterTheReconnectHandshakeTheSameMessagesStillCarryReadiness`。这三组刻画的是修前就成立、修后必须保持的行为，
  修前绿是对的。
