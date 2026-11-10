# control-server#877 证据索引

| 文件 | 内容 |
| --- | --- |
| `red-on-test-commit.txt` | 测试提交 d5aafbfc3 上 7 条新/改用例：6 红（失败原文），1 绿（高于强制线不告警，护栏） |
| `green-targeted.txt` | 最终 head 上本机定向 1636 条全绿（含 ArchitectureTests） |
| `mutations.txt` | 三次变异，各自只让对应用例变红：M1 桩暂停按周期查询不排除已恢复；M2 看板去掉「暂停已解除不告警」护栏；M3 清桩后从不记 2343 |

变异做法：实现已提交后逐个改一个文件、跑本票三个测试类（133 条）、`git checkout` 还原该文件，每次核对只改了 1 个文件。
