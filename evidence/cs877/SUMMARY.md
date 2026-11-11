# control-server#877 证据索引

| 文件 | 内容 |
| --- | --- |
| `red-on-test-commit.txt` | 测试提交 d5aafbfc3 上 7 条新/改用例：6 红（失败原文），1 绿（高于强制线不告警，护栏） |
| `red-on-charger-test-commit.txt` | 桩一侧（调度 11-10 并入）测试提交 669d915c0 上：新用例红，其余 116 条绿 |
| `red-on-review-l3-test-commit.txt` | 审查 L3 测试提交 fdef7ce3c 上：两条新用例红，其余 118 条绿 |
| `green-targeted.txt` | 审查返工实现提交 fc1185bb0 上本机定向 1682 条全绿（含 ArchitectureTests、入站处理器） |
| `mutations.txt` | A：审查存活的 P1、P2、P3、P5 补用例后全被杀；B：作者变异 M1～M4 与 L3-a～e 全被杀。每条写替换命中数、改动文件数、build／test 退出码 |

