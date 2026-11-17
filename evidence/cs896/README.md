# cs#896 证据：清单项 eqp（协议 7.0.0 候选）

分支 `feat/cs896-worklist-eqp`，起点 `fp/v2-impl@e3145773`。

| 提交 | 内容 |
| --- | --- |
| `5831242f` | 协议身份与 vendor 副本换到 7.0.0 候选 `a1facf86`；`WorklistEqpDisplayOnlyTests`（红） |
| `823824a2` | 实现：清单项按受理时的 EQP 填 `eqp`，无则 null；十二份 WirePin 重录 |

## 文件

- `test-runs.txt`：红（`5831242f` 上 4 条红：新类 3 条 + `EverySnapshotThePublisherSendsMatchesItsFrozenSchema`）、绿（`823824a2` 上 126 条与宽筛选 2453 条）。
- `mutations.txt`：四个变异，各自只改一个文件、跑完还原：
  - M1 清单项 `eqp` 写死为夹具值 `EQP-01`（相当于不取受理记录）→ 新类 3 条红；
  - M2 空串照发 → null 用例红；
  - M3 null 发成空串 → null 用例与「不据此判断」用例红；
  - M4 按 EQP 筛掉清单项（拿它做决定）→ null 用例与「不据此判断」用例红。
- `compare-wirepins.py`、`wirepin-compare.txt`、`wirepin-diff.txt`、`compare-selftest.txt`：WirePin 重录的判据与结果，见下。

## WirePin 重录

7.0.0 把清单项 `eqp` 定为必填可空，集成分支上没有这个键，所以照 cs#382 的先例在本票分支上录。判据由 `compare-wirepins.py` 执行：
去掉清单 item 的 `eqp` 后每条载荷逐字相同；表头、messageId、修订号、发件箱与线上顺序、RIoT 订单逐字相同；变了的只有那几条清单的
载荷摘要，线上一节每个变了的摘要都由发件箱一节同一 messageId 的载荷解释；新键次数等于旧基线里清单 item 的条数。十二份全部 PASS
（`normal-journey` 2 个、`two-journeys-one-vehicle` 4 个、其余各 1 个，值都是夹具的 `EQP-01`）。脚本自检：载荷改一个无关字符、
表头改一个修订号、删掉一个 `eqp`、线上一节改一个摘要，四种注入都报 FAIL。

## 核对项：升级重启后会不会对已有站点沿用旧 worklistRevision 重发清单

结论：**在「关闸且所有旅程 `Completed` 后才换装」的前提下不会发生。前提被违反时服务端也不会把同号不同内容的清单发给车，而是在自己这一侧报冲突。**
没有发现前提挡不住的路径。

服务端发 `CurrentStopWorklistSnapshot` 的入口只有四处：`JourneyRuntimeEngine.PublishStopWorklistAsync`（有项的清单，唯一构造清单项的地方
`JourneyRuntimeEngine.Worklist`）、`JourneyRuntimeEngine` 里刷新离站期限那一张（空清单）、`StopEndWorklist`（空清单）、`JourneyClosure`（空清单）。
另有两条「把已经落库的行再发一遍」的路：会话恢复时的 `OnboardJourneyPublisher.ReplayPendingForSessionAsync`，和旅程推进时按确定的 messageId
重新入队（`QueueEnvelopeAsync` → `WireToGateStore.QueueOutboundEnvelopeAsync`）。

1. **已完成的旅程不再推进。**引擎每轮只取 `Stage != Completed` 的旅程（`JourneyRuntimeEngine.cs:769`），所以已完成旅程的停靠不会重新生成清单，
   新构建里带 `eqp` 的那一版不会以旧号出现。
2. **旧行不会以旧号补发出去。**补发只改写信封的 `sessionGeneration` 与 `sentAt`，载荷原样（`OnboardJourneyPublisher.cs:55`），所以就算补发，
   内容也是车上那一版，不会多出 `eqp`。而且换装后服务端启动时 `ProtocolOutboxIdentityStartupCheck`（`Program.cs:288`）发现任何未确认、未退役的
   旧身份行就拒绝启动，旧行根本到不了补发。已确认或已退役的行从不补发。
3. **新旅程的号一定比车上所有的号大。**新旅程的清单修订号基准取按车计数器加每趟预留（`WireToGateStore.SeedSnapshotRevisionsAsync`，`:3679`），
   每发一张清单都会把计数器推到它之上（`RaiseSnapshotRevisionFloorAsync`，`:89`，收尾空清单也经过它）。所以换装后的第一张带 `eqp` 的清单
   用的是新号，车载端按新号采纳，不会撞上 `SNAPSHOT_REVISION_CONTENT_CONFLICT`。
4. **前提被违反时（旅程在途就换装）。**引擎会按确定的 messageId 重新入队同一版清单，新内容带 `eqp`、信封是新身份，与库里那一行不同；
   `WireToGateStore.RefreshOutboundEnvelopeAsync` 判为语义不同并抛 `ProtocolContentConflictException`（`WireToGateStore.cs:3920`），
   这一行不会被改写，也不会发出去。表现是该旅程在服务端推进失败，而不是车上判内容冲突。换装要求服务端与三台车同时换身份
   （`INCOMPATIBLE_EXACT_IDENTITY_REQUIRED`），旧会话本来也连不上，所以这条前提仍是必要的；本票不改这一行为。

附带一点（不是内容冲突，但与前提的表述有关）：前提写的是「所有旅程 `Completed`」。如果某车的最后一趟旅程收尾时车不在线，收尾那三张快照
在发件箱里是未确认的；换装后服务端会因为它们是旧身份而拒绝启动（`OUTBOX_PROTOCOL_IDENTITY_MISMATCH`，日志写明原因与出路）。这是安全的失败，
不会发错东西，但部署步骤里「所有旅程 Completed」之外还要「各车发件箱已确认」，与 `ProtocolCandidateIdentity` 注释里一直写着的
「Both outboxes are drained」是同一件事。
