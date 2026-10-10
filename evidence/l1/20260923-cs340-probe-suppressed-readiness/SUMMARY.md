# cs#340：哪些既有用例会走到「握手内压下就绪」

CI `test` run 35827392418 与 35827815860 各红一条：`JourneyRuntimeWorkerLoadDeadlineTests.AFailureReportedBeforeTheDeadlineIsNotSettledAsDeterminate`。它的夹具手工构造「会话中途」的连接状态，却漏设 `HandshakeCompleted`。这说明有些夹具会被本票的新判据走到，而单看 CI 绿红分不出来。所以让机器找一遍。

## 探针

在 `07177ee8`（已补上面那条夹具）上，往 `AnswerWithReadiness` 临时插一行：

```csharp
if (!state.HandshakeCompleted && (changed || announceUnchanged)) throw new InvalidOperationException("CS340-PROBE suppressed a readiness line");
```

这个条件恰好是「修前会附一行就绪、修后被压下」的那一刻。`--no-incremental` 编译得 `0 Error(s)`。随后在所有经由处理器的测试类上跑（共 25 个类，类名见 `probe-run.txt` 开头的过滤条件），跑完用 `git checkout` 还原。结果 441 条通过，15 条失败（同一用例的多个参数格各算一条）。

失败的 15 条分两类：

- 本票自己新加的握手用例：五处各一条，外加在途单未就绪那条。它们本来就是要走到这里。
- 下表 5 条既有用例。

## 既有用例逐条判定

| 用例 | 断言了答复行数或 `SessionReadiness` 吗 | 模拟的是 | 修后还绿，证明的还是原来那件事吗 |
| --- | --- | --- | --- |
| `JourneyRuntimeWorkerLoadDeadlineTests.AFailureReportedBeforeTheDeadlineIsNotSettledAsDeterminate` | **断言了**：`["DurableAck", "SessionReadiness"]` | 会话中途 | 修前红。夹具补上 `HandshakeCompleted = true` 后绿，证明的仍是原来那件事（`07177ee8`，注释写明不能删）。 |
| `JourneyRuntimeWorkerCargoRecoveryTests.AHandedOffDemandStillListedByMesIngestIsNeverDispatchedAgain` | 没有，只读第一行（`FirstLineType(ack) == "DurableAck"`）和库状态 | 会话中途（`Connection()` 没设标记） | 是。它证明的是交接之后需求终止、永不再派，走的处理路径和答复第一行都与握手标记无关。 |
| `JourneyRuntimeWorkerCargoRecoveryTests.AForcedRecoveryEndsTheDemandForGoodAndTheVehicleWorksAgainOnlyAfterItsHardwareRecord` | 没有，只读第一行的 `outcome`，再读库里的 `Readiness` | 会话中途（同一夹具） | 是。就绪由库判定，断言读的是库，不是答复行。 |
| `JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.AResultSentAfterAReconnectIsTakenAndSettlesTheStop` | 没有，只读第一行和库 | 重连之后的会话中途（`BeforeSublotConnection()` 没设标记） | 是。它证明结果被受理、只结算一次，与答复后面附不附就绪无关。 |
| `JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.AResultTakenBeforeTheLinkDroppedAndResentAfterTheReconnectIsSettledOnce` | 没有，只读第一行和库 | 同上 | 是，理由同上。 |
| `OnboardMessageProcessorTests.CompletedUnloadResultAtomicallyClosesDemandBeforeDurableAck` | 没有，只读第一行（注释原说它「会拿到一行就绪」） | 库里是刚 `BeginSessionRecoveryAsync`、还没有恢复报告的会话，按库状态其实就在握手中 | 是：它证明需求在确认之前关闭。注释已改成与 #340 一致（`b10683c9`）。 |

调度点名的另几处夹具，探针一条都没打到，因为它们受理的消息不经过 `AnswerWithReadiness`：

| 夹具 | 受理的消息 | 为什么走不到 |
| --- | --- | --- |
| `JourneyRuntimeWorkerTestKit.cs:1237`（`RequestLoadCorrectionOnConnectionAsync`） | `LoadCorrectionRequested` | 恢复请求，答复由 `ProcessRequestAsync` 给出，不附就绪 |
| `JourneyRuntimeWorkerLoadCommandCommitOrderTests.cs:73` | `LoadCancellationStartRequested` | 同上 |
| `ReversedDirectionJourneyRuntimeTests.cs:132` | `SublotSubmitted`、`LoadCancellationStartRequested` | 前者只回 `DurableAck`，后者同上 |
| `OnboardJourneyPublisherTests.cs:68/617/732` | `SnapshotAppliedAck`、`DurableAck` | 答复为空，断言 `Assert.Empty(response)` |

## 结论

应该是会话中途、却因标记为 false 而把判据变成「握手路径」的，只有表里第一条，已当票修掉。另外四条既有用例的夹具同样漏设标记，但它们证明的事与答复后面附不附就绪无关，所以这次不动。

这里留一个状态（推的）：这些夹具从 control-server#202 起，就不做握手后的触发发送了。等哪天有用例要在它们上面断言触发发送或就绪行，就必须先补上标记。这一点写进了 PR 的「剩余风险」。
