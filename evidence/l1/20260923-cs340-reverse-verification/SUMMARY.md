# cs#340 反向验证：每次注入一处故障，看哪些用例变红

脚本 `mutate.py`：每次做一处文本替换（必须恰好命中 1 处，否则整轮退出，免得「没注入上」看起来像绿），
`dotnet build --no-incremental` 确认 `0 Error(s)`，跑全部新用例，然后 `git checkout -- <file>` 还原。每一格的
差异在 `*.diff`，测试输出在 `*.test.txt`。

| 注入 | 意思 | 变红的用例 | 目录 |
| --- | --- | --- | --- |
| M0 | 组合函数不看 `HandshakeCompleted` | 五处各自的用例、在途单未就绪用例 | run1 |
| M1 | `SafetyStateChanged` 退回修前写法（自己拼就绪，无条件） | 它自己的用例、在途单未就绪用例、护栏 | run1 |
| M2 | `OperationResult` 退回修前写法 | 它自己的用例、护栏 | run1 |
| M3 | 恢复结果退回修前写法 | 它自己的用例、护栏 | run1 |
| M4 | 重复到达改绑退回修前写法 | 它自己的用例、护栏 | run1 |
| M5 | 硬件恢复记录退回修前写法 | 它自己的用例、护栏 | run1 |
| G1 | 在 `OperationProgress` 处新拼一行 `SessionReadinessLine`（第六处） | 护栏、在途单未就绪用例（它断言进度报告只回确认） | run1 |
| M6 | 组合函数永远不附就绪 | `AfterTheReconnectHandshakeTheSameMessagesStillCarryReadiness`、在途单未就绪用例 | run2 |
| P1 | 判 `READY` 不再要求本代次恢复报告（`WireToGateStore`，只为验前提用例） | `InsideTheReconnectHandshakeReadinessCannotChangeBeforeTheRecoveryReport` | run2 |

「退回修前写法」逐句还原了 `18172346` 上那一处的代码（先比就绪、再写连接状态、再拼 `SessionReadinessLine`），
只把那一处拿出组合函数，其余四处不动。M1～M5 各自只红自己那条行为用例，说明五条用例各守一处、互不代劳。

## run1 里两格不算数，run2 重做

- **M6 run1 编译失败**：替换成 `if (true)` 触发 `CS0162 Unreachable code`（警告当错误），测试没跑。run2 改成
  `if (Environment.TickCount64 >= 0)`，编译通过后照上表变红。`mutate.py` 是改过之后的版本。
- **P1 run1 零红**：当时前提用例只补发一条未知结果，它自己就让会话进恢复，把「缺恢复报告」这个前提遮住了。
  于是给前提用例加了一格「补发完成结果」（提交 `af995c34`）：两份快照一到，挡在 `READY` 前面的只剩缺失的恢复报告。
  run2 在 `af995c34` 上重做 P1，新加的那一格变红。

## 没有注入能单独打红的一条

`TheRecoveryReportAnnouncesTheReadinessAResendInsideTheHandshakeLeftBehind` 在以上每一格都绿。它的判别力来自自身的
两行对照：同一握手，补发未知结果时恢复报告答 `RECOVERY_REQUIRED`，不补发时答 `READY`，两个期望互斥，
任何一行被错误实现改变都会有一行红。
