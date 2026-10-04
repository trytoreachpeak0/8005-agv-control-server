# cs#454 自测证据

都是笔记本上跑 `scripts/parallel/Test-ParallelInstance.ps1` 的输出，没有碰任何机器。

| 文件 | 内容 |
| --- | --- |
| `selftest-red-before-fix.txt` | 清桩两节的测试先落（`a7b2953e`），在 `46148e35` 的脚本上：224 通过、42 失败 |
| `selftest-red-before-preflight-fix.txt` | 升级预检的测试先落（`85e8c84a`）：291 通过、3 失败 |
| `selftest-red-before-review-fixes.txt` | 审查补项的测试放在 `8d94860e` 的脚本上跑：12 项失败后，脚本在升级拒绝表那一段因 `Get-ParallelUpgradeRefusal` 不存在而中止，**之后的用例没有跑到**，所以这份红只覆盖前半 |
| `review-mutations.txt` | 审查点名的变异（M4、M5、M6、M8、M10、M12、N1、停服务与置 false 的先后、S3 两处）逐个注入、跑自测、还原：10 个全部被杀死 |
| `selftest-red-before-config-missing-fix.txt` | 配置缺失补项的测试先落（`85df8735`），在 `44705bce` 的脚本上：316 通过、11 失败 |
| `selftest-red-before-incremental-fixes.txt` | 增量审查补项：新测试加新模块、配**旧**安装脚本（`333509e8`）跑，安装脚本相关的 5 项红（凭据在记录定义之前、两个时间与服务状态的接线、回滚先查上一代、回滚查结果文件）。只用旧脚本整套跑会在拒绝表那一段因参数名不同而中止，那份不能说明问题，没有存。六个动作接线与「拒绝必须抛出」在旧脚本上本来就成立，是护栏，靠变异证明（见 `review-mutations.txt`） |
| `selftest-green-after-fix.txt` | 最终：360 通过、0 失败 |
