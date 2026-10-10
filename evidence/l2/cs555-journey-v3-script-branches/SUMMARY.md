# control-server#555 离线自检

`scripts/l2/Test-G3JourneyV3ScriptBranches.ps1`：几秒跑完，不起窗口，不用装置。两处脚本所在的分支在批次 8 出口（cs#393）之前没有任何一次运行走到过，journey 实跑要等 cs#393 移绑定之后。

| 文件 | 跑的是什么 | 结果 |
| --- | --- | --- |
| `green-after-fix.txt` | 本分支的两份场景脚本 | 11/11 ok，`PASS`，退出码 0 |
| `red-before-fix.txt` | 修复前的两份场景脚本（`git show 3411887d:<path>`，经 `-SlotFaultScenario`／`-ForcedScenario` 传入） | 3 条 `BAD`（正好是读场景脚本语法树的那 3 条），退出码 1 |

两组「事实」类用例在改前改后都是 ok，它们证明的是脚本缺陷的原因：

- `Get-L2Inbound` 的行只有 `MessageId`、`At`、`Payload`、`Response`、`ResponsePayload`。这一点从函数的语法树里读，不是抄的。在 StrictMode 下读 `.PayloadJson` 会抛错。
- 用真实 SQLite 文件、真实的 `Invoke-L2Query` 与 `Get-G3Scalar` 去读：NULL 读回来是空字符串，所以旧判断对 NULL 永远为假；`ClosedReason IS NULL` 对 NULL 为真，对空字符串和原因码都为假。

SQLite 用例借用 `src/ControlServer.Host` 的 Release 构建里的 `Microsoft.Data.Sqlite`。没有构建时这些用例报 skip，退出码 2，不会被当成通过。
