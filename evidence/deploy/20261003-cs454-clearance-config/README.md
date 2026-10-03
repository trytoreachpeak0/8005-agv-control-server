# cs#454 自测证据

都是笔记本上跑 `scripts/parallel/Test-ParallelInstance.ps1` 的输出，没有碰任何机器。

| 文件 | 内容 |
| --- | --- |
| `selftest-red-before-fix.txt` | 清桩两节的测试先落（`a7b2953e`），在 `46148e35` 的脚本上：224 通过、42 失败 |
| `selftest-red-before-preflight-fix.txt` | 升级预检的测试先落（`85e8c84a`）：291 通过、3 失败 |
| `selftest-red-before-review-fixes.txt` | 审查补项的测试放在 `8d94860e` 的脚本上跑：12 项失败后，脚本在升级拒绝表那一段因 `Get-ParallelUpgradeRefusal` 不存在而中止，**之后的用例没有跑到**，所以这份红只覆盖前半 |
| `review-mutations.txt` | 审查点名的变异（M4、M5、M6、M8、M10、M12、N1、停服务与置 false 的先后、S3 两处）逐个注入、跑自测、还原：10 个全部被杀死 |
| `selftest-green-after-fix.txt` | 最终：317 通过、0 失败 |
