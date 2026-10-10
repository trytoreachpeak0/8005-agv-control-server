# cs#556 证据

| 目录 | 内容 |
| --- | --- |
| `red/01-l1-red-at-9989081c.txt` | 测试提交 `9989081c`（修复前）上 L1 定向跑：3 条新用例红，原因码 `FORCED_RECOVERY_GENERATION_MISMATCH`；2 条护栏绿 |
| `mutations/` | `mutate.ps1` 与 `mutations.txt`：M0（整段删除）与 M1～M4（只升不降、HistoricalOnly、管理员关闭过的工作流、会话代数）各被对应用例杀死，每个变异都重新构建 |
| `g3/red-at-4c0f5fa6/` | 真装置 `g3-forced-mechanical-recovery`，服务端 `4c0f5fa6`（新判据、修复前），车载端 `535c94fc`，模拟器 `fb5f7c59`：只有 `G3-07-44` FAIL，读到 `FORCED_RECOVERY_GENERATION_MISMATCH`、报过的代数 0 |
| `g3/green-at-3adace55/` | 同一场景，服务端 `3adace55`（修复 + merge `fp/v2-impl`），同样两个对端：`G3-07-41`～`45` 全 PASS，读到 `FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED`、报过的代数 1 |

两遍 G3 都是用 `Invoke-L2Scenario.ps1` 直接跑真装置场景，不是 `run-journey-g3.ps1`，所以不是门禁证据。journey G3 的场景脚本取自绑定提交，绑定由下一次出口移动，届时 `G3-07-44` 才进入正式证据。只保留 `SUMMARY.md` 与 `assertions.json`。
