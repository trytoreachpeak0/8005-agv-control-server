# cs#777 证据

| 项 | 结果 | 文件 |
| --- | --- | --- |
| 定向测试（`TaskTypeAdmissionRuntimeTests`、`AdmissionPolicyVersionGuardTests`、`TaskTypeStationStartupTests`） | 45/45 通过 | 见 PR 正文 |
| `scripts/parallel/Test-ParallelInstance.ps1` | 1210 passed, 0 failed | `parallel-selftest-green.txt` |
| 取红：生产定义 8 → 7 | 守卫 `EveryInstanceDefinitionShipsTheLastVersionOfTheHistory` 与顺序用例 `TheFullInspectionActivationDoesNotDriftWhenActivatedStoppedAndStartedUnderTheRaisedVersion`、`ActivatingWhileTheOldVersionStillRunsDriftsUntilTheRaisedVersionStarts` 变红（`ACCEPTED` 变成 `ADMISSION_POLICY_DRIFT`）；还原后 20/20 通过 | `mutation-definition-7.txt` |
