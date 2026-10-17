# agv02 取消装货被拒（BUSINESS_ID_CONTENT_CONFLICT）只读取证 2026-10-15

来源：v2 库只读（ro-sql.ps1，sqlite-tools 副本，Mode=ReadOnly）；agv02 日志簿复制件 d2/agv02-journal-copy/onboard-journal.db（车上原文件未动）；agv02 车载端日志 C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261015.log 片段（d2/agv02-journal-copy/log-ctx.txt）。原始输出：56-*、57-*、58-*、59-*。

## 时间线（UTC）
- 07:12:54 服务端按站点期限释放 e9bd5f2c、67e7c463（2310）。
- 07:13:00.86 车上应用 CurrentStopWorklistSnapshot rev 136：站「派工待送取货」(217)，operationSessionId 31e06c38-1659-ae56-9a2a-4a03724d63aa，items = 2cfe544c(Q26101158-5)、68797314(Q26091200-3)。
- 07:13:20.90 第一次按取消：LoadCancellationStartRequested 794a5ea7，cancellationId 7a2376ce-6178-f95d-b3b5-e8eec7ee3e61，demandId 2cfe544c，slotOperationAttemptId null，operator S00***** SESSION verifiedAt 07:13:20.8373558，reason「现场确认装货取消，申请将目标仓位清空。」→ AUTHORIZED。07:13:21.00 LoadCancellationResult → RecoveryWorkflows 7a2376ce LOAD_CANCELLATION State=Reconciled Outcome=ALL_EMPTY，RequestMessageId 794a5ea7，RequestContentHash 7a236d34…，CreatedAt 07:13:20.9096768。服务端 JourneyDemands 2cfe544c → TERMINATED。
- 07:13:22.08 车上只收到 UpcomingStopPlanSnapshot rev 196；**CurrentStopWorklistSnapshot 没有更新，仍是 rev 136（仍列 2cfe544c）**。车载日志 07:13:16 起每 2.5 s 一行「服务端请求录入Sublot … revision=136，expectedSublots=Q26101158-5、Q26091200-3」一直持续到 07:14:19 之后。
- 07:13:35.35、07:14:16.80、07:15:42.78 再按三次 → 服务端 1105 拒收（94b9abb0、c60d9b6a、1eb5d21b），车载日志「恢复向量LOAD_CANCELLATION未执行：reason=BUSINESS_ID_CONTENT_CONFLICT」。被拒消息服务端不留（ProtocolInbox 无行）。
- 车上日志簿 WireToGateRecoveryState（UpdatedAt 07:13:35.316）的 pendingLoadCancellation = {cancellationId 7a2376ce…, slotOperationAttemptId null, operatorId S00*****, SESSION, **operatorVerifiedAt 07:13:35.3138453**, reason 同上}。

## 结论
1. 被拒的 cancellationId 就是第一次已成功、已 Reconciled 的 7a2376ce（同 demand 2cfe544c + 同 operationSessionId 31e06c38 → StableUuid 相同）。
2. 与库里那行比，唯一不同是 operator.verifiedAt（07:13:20.837 → 07:13:35.314）。第一次成功后车上清掉了 pending 记录，第二次按时重新记录了一份带新 verifiedAt 的载荷，RecallOrRecord 之后的第三、四次沿用 07:13:35 那份，所以三次都冲突。哈希没算：服务端 RequestContentHash 的输入规约我没在库里看到；差异字段已经直接看到。
3. 根因链：取消成功后服务端没有给车发新的 CurrentStopWorklistSnapshot，屏上仍把已取消的 2cfe544c 列在首位 → 操作员以为没取消成功、再按 → 同一 id 新内容 → 冲突。就算哈希一致，那也只是重放一条已完成的取消，68797314 不会被取消。
4. operationSessionId：车上 worklist 的 31e06c38 自 07:13:00（07:12:54 释放之后）未变；服务端 JourneyRuntimes.OperationSessionId 是 353bc586-af07-3457-b4c0-5a17e2414b80（与车上不同，可能是另一个层级的会话，未深究）。今天早些时候 2cfe544c 没有别的取消记录；全库 8 条 LOAD_CANCELLATION 各 id 只出现一次，都是首按成功，早上没有重按。
