# cs#737 证据索引：服务端接入第七类 WIRE_TO_FULL_INSPECTION

协议身份：5.0.0 候选，`8005-agv-protocol` 提交 `9d7915786b736a6fa8d0c8737bfb115ac08a30b3`（protocol#22），manifest
`743b886d99194038b893e98aed2a1dd01409cd7f80d39c734e02d860e0e5827c`，`ApprovalStatus SUPERSEDING_CANDIDATE`。
下面的 G2／L2 证据一律是 `UNRELEASED_CANDIDATE`。

## L1

| 文件 | 内容 |
| --- | --- |
| `l1-red-test-commit-failures.txt` | 测试提交 `348846744` 上定向跑 80 条、38 条失败的失败行（当时协议身份是 `4ea9ac5d`，与 `9d791578` 的差别只在 manifest） |
| `guard-red-types-without-version.txt` | 只改类型清单、不提准入策略版本时 `AdmissionPolicyVersionGuardTests` 的两条失败原文 |
| `l2-tts-selftest-red.txt` | 测试提交上 `scripts/l2/Test-L2TaskTypeStations.ps1` 的失败行（默认预置只有六条规则） |
| `parallel-selftest-green.txt` | 改完 `scripts/parallel` 后 `Test-ParallelInstance.ps1` 的结尾：1203 passed, 0 failed |

实现之后，同一组定向用例加有效配置、协议身份几类共 162 条中只剩一条失败；那条是测试改成七类时同一个类里另一处
还钉着 6，改正后 `ParallelInstanceEffectiveConfigurationTests` 单跑 61/61。全量走 CI，见 PR。

## L2（合成装置，本机时段 10-16 23:25～23:33，控制端 `3d439fbb0`）

取红用的是注入故障：把 `WIRE_TO_FULL_INSPECTION` 从 `ExecutableTaskTypes` 删掉一行，跑完还原。

| 目录 | 结果 | 说明 |
| --- | --- | --- |
| `l2-red/same-direction-wire-to-full-inspection-journey/` | FAIL，exit 1 | `L2-SDJ-WFI-REAR-01` 不成立：需求积压原因 `TASK_TYPE_NOT_YET_EXECUTABLE` |
| `l2-red/same-direction-four-type-mixed-load-multi-drop/` | FAIL，exit 1 | 甲、乙、丙三单照常进同一趟；丁（第七类）没能加入，积压原因 `TASK_TYPE_NOT_YET_EXECUTABLE` |
| `l2-green/same-direction-wire-to-full-inspection-journey/` | PASS，exit 0 | 两条需求各在机台站 12 按侧取仓、到模拟站 405 卸货、结清 |
| `l2-green/same-direction-four-type-mixed-load-multi-drop/` | PASS，exit 0 | 四单同一趟；丁的取货是 12 号站上排在丙之后的第 4 个停靠；卸货依次 305、13、210、406；四单都 Succeeded |

每份只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`；整份日志与库快照留在本机 `F:/8005-scratch/cs737-l2/`，不入库。
