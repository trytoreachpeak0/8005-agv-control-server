# cs#277 第 1 步：替身拒绝命令时，响应正文要进证据

## 要证明什么

`L2Double.Command` 在替身返回非 2xx 时，把响应正文带进抛出的异常；编排器把那条异常消息写进
`SUMMARY.md` 的「失败原因」与 `timeline.jsonl` 的 `Run failed` 一行，于是正文进了证据。

改动之前，pwsh 把正文放在 `ErrorDetails` 里而不在异常消息里，编排器只写异常消息，所以
`load-cancelled-before-sublot` 两次红的证据里都只有一句 `409 (Conflict)`，看不到合成车载端
回的 `CONNECTION_CHANGE_FAILED` 和它包进 `detail` 的异常。

## 怎么跑的

一次性场景 `refusal-body-reaches-evidence.ps1`（本目录）：环境起来后，向**真正的**合成车载端
发一条 `commandId` 为空白的重连命令。`/connection` 按设计以 `INVALID_ARGUMENT` 拒绝（400，
带正文），场景随即中断，编排器照常写证据。

场景不进仓库的 `scenarios/`（它按设计必红）。跑法是把 `scripts/l2/` 复制到临时目录、场景放进
副本的 `scenarios/`，运行副本的 `Invoke-L2Scenario.ps1` 并传 `-Repository` 指回本 worktree：

```
pwsh -NoProfile -File <副本>/scripts/l2/Invoke-L2Scenario.ps1 -Scenario refusal-body-reaches-evidence -Repository <worktree> -EvidenceRoot <新目录>
```

两份 `SUMMARY.md` 里的 `controlServerCommit` 都是 `e9df58cd`（集成分支顶端）：本票只改脚本，
服务端与三个替身的代码就是那一版。两次运行只差副本里那一份 `L2.psm1`。

## 结果

| 文件 | 副本里的 `L2.psm1` | 失败原因 |
| --- | --- | --- |
| `SUMMARY-with-change.md` | 与本 PR 的 `scripts/l2/L2.psm1` 逐字节相同 | `... 400 (Bad Request). [fake-onboard PUT connection] Response body: {... "reasonCode":"INVALID_ARGUMENT"}}` |
| `SUMMARY-base.md` | 与 `origin/fp/v2-impl` 的 `scripts/l2/L2.psm1` 逐字节相同 | `... 400 (Bad Request).`，没有正文 |

**对照那一份是必要的**：只看改动后那一份，证明不了正文是因为这次改动才进了证据，而不是编排器
本来就会写。

注意这里拿到的是 400 而不是 409：`/connection` 对空白 `commandId` 回 400（`INVALID_ARGUMENT`），
对重连失败回 409（`CONNECTION_CHANGE_FAILED`）。两者走的是同一段代码——只有替身声明要带
`expectedRevision` 且状态码是 409 时才重试，合成车载端不声明，所以两种状态码都是第一次就抛出。
409 这一支由 `scripts/l2/Test-L2DoubleCommandError.ps1` 覆盖。
