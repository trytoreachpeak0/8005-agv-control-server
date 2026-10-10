# L2 场景证据：sublot-rejected-after-entry

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T165407837Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-5` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T165407837Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理时冻结的花篮数量是 1（4 箱 ÷ 每篮 4 箱），仓位也已按它预留 | PASS | `ExpectedBasketCount=1 / TargetSlotsJson 非空` | `ExpectedBasketCount=1 / TargetSlotsJson=[1]` |
| 到站停在等录入，录入请求给的是本次派车范围的子批号、没有需求号 | PASS | `车收到 >= 1 次 / [L2-SRJ-20261009T165407837Z]` | `车收到 1 次 / [L2-SRJ-20261009T165407837Z]` |
| 服务端拒收并发出 SublotRejected：原因是 EXPECTED_BASKET_COUNT_MISMATCH，correlationId 是被拒那条提交的 messageId，rejectedSublot 是录入值 | PASS | `EXPECTED_BASKET_COUNT_MISMATCH / 3bdd8ab6-13df-40b9-b5aa-3f406cd4f5ee / L2-SRJ-20261009T165407837Z / 车收到` | `EXPECTED_BASKET_COUNT_MISMATCH / 3bdd8ab6-13df-40b9-b5aa-3f406cd4f5ee / L2-SRJ-20261009T165407837Z / demandId=21465704-9fdc-4324-ac58-38f15ee95942 / 车收到 1 次` |
| 被拒的那条提交带的是本站地址、带 currentWorklistRevision | PASS | `sublot=L2-SRJ-20261009T165407837Z / revision=1 / AGV-L2-001` | `sublot=L2-SRJ-20261009T165407837Z / revision=1 / AGV-L2-001` |
| 拒收不发装货命令、不建仓位操作、不动已预留的仓位，旅程留在等录入 | PASS | `AwaitingSublot / 0 / 0 / [1] / 1` | `AwaitingSublot / 0 / 0 / [1] / 1` |
| 拒收不写阻断原因（被拒的提交不是旅程的阻断，它不上看板） | PASS | `无阻断原因` | `BlockReasonCode=(null)` |
| 范围外的子批号：SUBLOT_NOT_IN_DISPATCH_SCOPE、demandId 为空、rejectedSublot 是录入值 | PASS | `SUBLOT_NOT_IN_DISPATCH_SCOPE / demandId=null / L2-SRJ-OUTSIDE-20261009T165407837Z / 提交与拒收对得上` | `SUBLOT_NOT_IN_DISPATCH_SCOPE / demandId= / L2-SRJ-OUTSIDE-20261009T165407837Z / correlationId=cd322362-b772-4818-b209-c35f321aa8b9` |
| 范围外的拒收也是新的一条提交（新的 messageId），不是把上一条重发一遍 | PASS | `1 条新提交 / 与第一条不同` | `1 条 / cd322362-b772-4818-b209-c35f321aa8b9` |
| 两次拒收之后仍然没有装货命令、没有仓位操作，旅程仍在等录入 | PASS | `0 / AwaitingSublot` | `0 / AwaitingSublot` |
| 重扫之后发的是装货命令：给的是这条需求与重扫那条子批号，仓位数是受理时冻结的值 | PASS | `21465704-9fdc-4324-ac58-38f15ee95942 / L2-SRJ-20261009T165407837Z / 1` | `21465704-9fdc-4324-ac58-38f15ee95942 / L2-SRJ-20261009T165407837Z / 1` |
| 被拒过的子批号重扫之后照常受理：旅程离开等录入，消费的是重扫那条新提交 | PASS | `ConsumedSublotMessageId=fc358a2a-328a-4531-b33b-020481d8e5f3（新提交）` | `ConsumedSublotMessageId=fc358a2a-328a-4531-b33b-020481d8e5f3 / 重扫提交=fc358a2a-328a-4531-b33b-020481d8e5f3` |
| 装载操作提交，且全程只有一条装货尝试 | PASS | `Committed / 1 条` | `Committed / 1` |
| 同一条提交只拒一次：三次录入对应两条拒收，被拒的正是前两条，受理的那条没有拒收 | PASS | `2 条拒收 / 3 条提交 / 被拒的是前两条` | `2 条拒收 / 3 条提交 / 被拒 3bdd8ab6-13df-40b9-b5aa-3f406cd4f5ee, cd322362-b772-4818-b209-c35f321aa8b9 / 受理 fc358a2a-328a-4531-b33b-020481d8e5f3` |
| 全程没有 SUBLOT_SUBMISSION_MISMATCH：被拒过的提交留在库里，之后每一轮都还会被读到，旅程的阻断原因始终为空 | PASS | `无阻断原因` | `BlockReasonCode=(null)` |
| 全程只建了一条 RIoT 单（取货），没有重复派车，也还没有关卡单 | PASS | `1` | `1 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
