# cs#202 红证据：注入故障，删掉补偿授权处的 CLOSED 拦截

- 提交：`c18a48f1`，本地临时提交，从未推送，跑完即撤；在 `83e42c11` 上把
  `OnboardRecoveryCoordinator.AuthorizeLoadCompensationAsync` 的 `if (session.State == "CLOSED")` 改成
  `if (false && ...)`，差异见 `injected-fault.diff`
- 测试：`ACompensationAuthorizedAfterItsSessionClosedIsRefusedAndDoesNotReopenIt`（已改为活路径搭场景）
- 结果：红，`Expected: "CLOSED"  Actual: "EXECUTING"`——会话被重开。原文见 `console.txt` 与 `red.trx`

说明改写后的测试不再靠 `ProcessAsBeforeCs187Async` 也能守住这道拦截。
