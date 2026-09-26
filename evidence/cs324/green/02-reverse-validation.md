# cs#324 反向验证（L1）

每次只注入一处，备份还原，`dotnet build --no-incremental` 后 `dotnet test --no-build`，范围是
`StopEndedJourneyContinuesTests` 与 `JourneyRuntimeWorkerLoadCancellationBeforeSublotTests`。注入前先写下预期。

| 注入 | 预期红 | 实际红 |
| --- | --- | --- |
| M1 去掉号数顺延（`WorklistRefills` 不加） | 号数那条；重连补发可能连带 | `AnEndedStop…`、`AfterAReconnect…`、`WhenTheNextStopCarriesNone…`（失败文本：卸货站清单第 3 号没有越过空清单的第 3 号） |
| M2 不暂存空清单 | 空清单、号数、重连、B 形态迟到扫码 | 同预期 4 条 |
| M3 入站不判迟到扫码 | 两条迟到扫码 | 同预期 2 条 |
| M4 取消原因码恒为旧码 | 两条迟到取消 | 同预期 2 条 |
| M5 去掉「阶段仍在等录入就不抢答」 | 护栏那条 | **没有红** |

两处与预期不符，如实记在这里：

- **M1 起初没有让 `TheNextStopsWorklistStartsAboveTheEmptyOne` 变红。**那条用例里甲、乙共用一个卸货站，卸货站把已终结的乙
  也算作「做完」，号自然多出一号，碰巧躲开冲突。所以补了 `WhenTheNextStopCarriesNoneOfTheEndedDemandsItStillStartsAboveTheEmptyWorklist`
  （乙有自己的卸货停靠、终结后被计划删掉），它在 M1 下红在同号上。
- **M5 存活——当时给的解释是错的，已由审查指出、在下一节改正。**当时写的是「处在 `AwaitingSublot` 的停靠按构造总有待做项，
  这一行是多余的防御」。这个前提不成立：扫码前取消结束本站时，协调器只终结需求、不改阶段，旅程带着 0 个待做项停在
  `AwaitingSublot` 直到离站期限。M5 存活的真正原因是**用例只走了期限那一路**（期限会把阶段推走），没有一条用例走取消那一路。
  这一行不是多余防御，而是缺陷：它让那段时间里的迟到扫码漏过入站、交给引擎。

## 与 cs#339 期限重填的叠加（合并 fp/v2-impl `3eaf368c` 之后补，调度要求）

`ARefillBeforeTheStopEndsAndTheEmptyWorklistStackAndTheRevisionsStillOnlyAdvance`：第二个取货站到站发 F，断线重连重填后发 F+1（新期限），
期限结束发空清单 F+2，这一站 `WorklistRefills` = 2，下一站首号 F+3。修后绿。

M1（去掉号数顺延）下它红在行为判据上：`下一站首号 4 没有越过空清单的第 4 号。`（第一次写时 `WorklistRefills == 2` 那句排在前面、先红，
挪到最后，让行为判据先说话。）

反过来的顺序（先空清单、再重填）在同一站上不会发生：重填只在本停靠还有待做项时升版，空清单只在待做项归零时发。

## 审查之后（PR #361 两路审查，head `7dc4a56a` 之后）

### 必修：去掉「阶段是等录入就交给引擎」那一支

`LateSublotSubmission.StopEndedAsync` 不再看阶段，只看「当前停靠是不是这一站、还有没有待做项」。新用例
`AfterACancellationEndsTheStopALateEntryIsRejectedAsStaleAtTheEmptyWorklistsRevision`：多需求、第二个取货站扫码前取消（B 形态），
断言阶段仍是 `AwaitingSublot`（审查说的那个形状）、协调器在结果应答之后当场把空清单发上线、随后的迟到扫码答
`WORKLIST_REVISION_STALE` 且号是空清单的号、引擎再转一轮也不补答第二条。

**M5 重做，这次红了**：放回那一支，新用例红在 `Assert.Single() Failure: The collection was empty`（入站没有答这条迟到扫码）。
审查说退回之后引擎会按 `SUBLOT_NOT_IN_DISPATCH_SCOPE`、号 r+1 答复——那是审查读码推出的；这次注入的红点停在入站那一步，
没有再跑引擎一轮去核实引擎怎么答。

### 审查 S1：本站结束时一并答复没人答的扫码

`LateSublotSubmission.StageForUnansweredEntriesAsync`：本站结束的那一次改动里（写锁之内），把答这一站、既没被装货命令消费、也没被拒收的录入
一并暂存 `WORKLIST_REVISION_STALE`。接在两个出口上：旅程继续（`StopEndWorklist`，B 形态）与整趟收尾（`JourneyClosure`，A 形态）。
保存之后由 `LateSublotSubmission.SendStaleAnswersAsync` 发出。

交错用例用一个数据库命令拦截器构造窗口：引擎这一轮读完收件箱里的录入、读取器关闭的那一刻，从另一个上下文落一条录入，然后引擎才进期限写锁。
用例断言拦截器恰好触发一次、这条录入没被装货命令答复（它确实落在窗口里），再断言它被答 STALE、号是空清单（或收尾清单）的号、已上线。

### 本轮注入（注入前写下的预期 → 实际）

| 注入 | 预期红 | 实际红 |
| --- | --- | --- |
| M5 放回「阶段是等录入就交给引擎」 | 扫码前取消后的迟到扫码 | 同预期，红在「入站没答」 |
| M6 空清单那一处不答没人答的扫码 | B 形态交错 | 同预期 |
| M7 收尾那一处不答 | A 形态交错 | 同预期 |
| M8 本站结束后不发这些拒收 | B 形态交错，红在「上线」那一句 | 同预期（`Assert.Contains() Failure`） |
| M9 协调器不当场发空清单 | 扫码前取消那条，红在「结果应答之后上线」那一句 | 同预期（`Assert.Contains() Failure`） |
| M1 去掉号数顺延（重跑，范围含全部新用例） | 号数两条 | 号数两条（「卸货站清单第 3 号没有越过空清单的第 3 号」「下一站首号 4 没有越过空清单的第 4 号」），另四条红在「空清单没上线」：不顺延时发送一侧按号推算出的是上一版非空清单，于是不发 |

原始输出在 `02-reverse-validation-raw.txt`。
