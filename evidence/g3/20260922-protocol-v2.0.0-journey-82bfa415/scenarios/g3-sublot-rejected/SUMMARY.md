# L2 场景证据：g3-sublot-rejected

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T074504241Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `82bfa41511e515aa1063a9acfd900bd7b443d933` |
| onboardHmiCommit | `86d42ce5362a8273525b8ba1acb387e4331bcfed` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T074504241Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：SublotEntryRequested → SublotSubmitted → SublotRejected，提交恰好一条；拒收的 correlationId 就是 UIA 录入的那条提交的 messageId（CV-SUBLOT-REJECTED-AFTER-ENTRY orderedExpectedMessages） | PASS | `EntryRequested < Submitted×1 <= Rejected，correlationId = 提交 messageId` | `EntryRequested×1 / Submitted×1 / 有序=True / correlationId=425f2c77-c922-44c3-8ea5-caff5e103fb7 提交=425f2c77-c922-44c3-8ea5-caff5e103fb7` |
| 录入之后重新校验：容量对照改成 2 箱/篮后重算得 2 个花篮，与派车冻结的 1 不符，拒收原因 EXPECTED_BASKET_COUNT_MISMATCH，指名这条需求、被拒的子批、本站的操作会话与修订号（REVALIDATE_SUBLOT_AFTER_ENTRY） | PASS | `EXPECTED_BASKET_COUNT_MISMATCH / 8958d0ee-2958-4bc8-83b7-3dad64ff4f45 / G3-02R-20260922T074504241Z / 会话一致 / revision 1` | `EXPECTED_BASKET_COUNT_MISMATCH / 8958d0ee-2958-4bc8-83b7-3dad64ff4f45 / G3-02R-20260922T074504241Z / 会话一致=True / revision 1` |
| 拒收的录入不开仓：0 笔仓位操作、0 条 SlotOperationCommand，模拟器上每个仓与到站时逐仓相同；旅程仍在等录入，预留的仓位与冻结的花篮数一点没动，没有阻断原因（NEVER_UNLOCK_ON_REJECTED_ENTRY；forbidden: duplicate-slot-unlock、expanded-active-unlock-set） | PASS | `操作 0 / 命令 0 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0 3=CLOSED/EMPTY/1/0 4=CLOSED/EMPTY/1/0 5=CLOSED/EMPTY/1/0 6=CLOSED/EMPTY/1/0 7=CLOSED/EMPTY/1/0 8=CLOSED/EMPTY/1/0 / AwaitingSublot / [1] / 1 / 无阻断` | `操作 0 / 命令 0 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0 3=CLOSED/EMPTY/1/0 4=CLOSED/EMPTY/1/0 5=CLOSED/EMPTY/1/0 6=CLOSED/EMPTY/1/0 7=CLOSED/EMPTY/1/0 8=CLOSED/EMPTY/1/0 / AwaitingSublot / [1] / 1 / 阻断=` |
| 车载端显示服务端给的拒收原因：提示区的 SublotRejectionReason 在 UIA 树里，ItemStatus 是服务端发的原因码，文字非空且点出被拒的子批（DISPLAY_SERVER_REJECTION_REASON；不比文案全文） | PASS | `ItemStatus=EXPECTED_BASKET_COUNT_MISMATCH，Name 非空且含 G3-02R-20260922T074504241Z` | `Name=「子批 G3-02R-20260922T074504241Z 被服务端拒收：花篮数量与已预留仓位数不符。」 ItemStatus=EXPECTED_BASKET_COUNT_MISMATCH` |
| 拒收之后录入保持打开、可以重扫：车载端录入框可用，服务端仍在等这一站的录入（KEEP_ENTRY_OPEN_FOR_RESCAN） | PASS | `录入框可用 / AwaitingSublot` | `录入框可用=True / AwaitingSublot / 录入请求 AcknowledgedAt=` |
| 容量对照恢复后重扫照常装货：重扫是一条新提交，服务端消费的正是它，装货命令按冻结的 1 个花篮下发，车载端开的就是授权的仓、装完关着有货锁上，装载 Committed；车载端提示区撤下了拒收原因 | PASS | `提交 2 条、消费第 2 条 / 1 仓 / 等待时 OPEN / 1=CLOSED/OCCUPIED/1/0 / Committed / 拒收提示已撤` | `提交 2 条、消费 486a14a6-4781-4bdc-9d23-1bef72aa2e2f / 1 仓 / 等待时 OPEN/EMPTY/0/0 / 1=CLOSED/OCCUPIED/1/0 / Committed / 拒收提示=(已撤)` |
| 终态没有重复提交：全程 1 条拒收（回答第一条提交）、1 笔仓位操作、1 条装货命令、1 份 COMPLETED 结果，开锁只有授权的仓各一次，RIoT 上只有那一张取货单，没有阻断原因、没有 RecoveryRequired（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-riot-order、duplicate-slot-unlock、duplicate-business-commit） | PASS | `拒收 1 / 操作 1 / 命令 1 / 结果 1 COMPLETED / 开锁 1 / RIoT 单 1 / 无阻断 / RecoveryRequired 0` | `拒收 1 / 操作 1 / 命令 1 / 结果 1 COMPLETED / 开锁 1 / RIoT 单 1 / 阻断= / RecoveryRequired 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
