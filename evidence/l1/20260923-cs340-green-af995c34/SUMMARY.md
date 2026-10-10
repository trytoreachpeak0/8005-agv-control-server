# cs#340 本机绿证据（`af995c34`）

服务端全量测试按工作区规则只走 CI，本机只跑受影响的测试类与改动文件的格式检查。

- `test-classes.txt`：`dotnet build --no-incremental` 后跑 `RecoveryStateMachineG2Tests`、`OnboardMessageProcessorTests`、
  全部 `*ArchitectureTests`、`OnboardTcpServerTests`、`OnboardHandshakePushGateTests`、`ExpectedActionOverdueTests`、
  `ArrivalPublishInterruptedThenReconnectedTests`、`SessionReadinessReasonCodesTests`：

  ```
  Passed!  - Failed:     0, Passed:   277, Skipped:     0, Total:   277
  ```

- `dotnet-format-verify.txt`：对四个改动文件跑 `dotnet format --verify-no-changes --include ...`，报 3 条 `WHITESPACE`，
  都在 `OnboardMessageProcessor.SerializeEnvelope`（`git blame` 指向 `8fae66f6`、`f99edf34`，本票之前就有；control-server#202
  的关闭评论记过同样三条）。本票新增和改动的代码零条，测试文件零条。按冲突边界不顺手改。
