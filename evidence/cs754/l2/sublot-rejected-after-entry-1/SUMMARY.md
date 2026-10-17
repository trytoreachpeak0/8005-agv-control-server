# L2 场景证据：sublot-rejected-after-entry

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T183348939Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `6b71be5175eb90151ae01dd050658ee99563fe7e` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T183348939Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理时冻结的花篮数量是 1（4 箱 ÷ 每篮 4 箱），只按它预留数量、不定仓号（control-server#754） | PASS | `ExpectedBasketCount=1 / TargetSlotsJson=[]` | `ExpectedBasketCount=1 / TargetSlotsJson=[]` |
| 到站停在等录入，录入请求给的是本次派车范围的子批号、没有需求号 | PASS | `车收到 >= 1 次 / [L2-SRJ-20261017T183348939Z]` | `车收到 1 次 / [L2-SRJ-20261017T183348939Z]` |
| 服务端拒收并发出 SublotRejected：原因是 EXPECTED_BASKET_COUNT_MISMATCH，correlationId 是被拒那条提交的 messageId，rejectedSublot 是录入值 | PASS | `EXPECTED_BASKET_COUNT_MISMATCH / bdf1adea-637f-4d43-8ef9-22cd2ebc8342 / L2-SRJ-20261017T183348939Z / 车收到` | `EXPECTED_BASKET_COUNT_MISMATCH / bdf1adea-637f-4d43-8ef9-22cd2ebc8342 / L2-SRJ-20261017T183348939Z / demandId=3d25d1d0-0bb7-433f-9f89-a0e563259f13 / 车收到 1 次` |
| 被拒的那条提交带的是本站地址、带 currentWorklistRevision | PASS | `sublot=L2-SRJ-20261017T183348939Z / revision=1 / AGV-L2-001` | `sublot=L2-SRJ-20261017T183348939Z / revision=1 / AGV-L2-001` |
| 拒收不发装货命令、不建仓位操作、不分仓位（仍是受理时的 []），旅程留在等录入 | PASS | `AwaitingSublot / 0 / 0 / 归属行 [] / 1` | `AwaitingSublot / 0 / 0 / 归属行 [] / 1` |
| 拒收不写阻断原因（被拒的提交不是旅程的阻断，它不上看板） | PASS | `无阻断原因` | `BlockReasonCode=(null)` |
| 范围外的子批号：SUBLOT_NOT_IN_DISPATCH_SCOPE、demandId 为空、rejectedSublot 是录入值 | PASS | `SUBLOT_NOT_IN_DISPATCH_SCOPE / demandId=null / L2-SRJ-OUTSIDE-20261017T183348939Z / 提交与拒收对得上` | `SUBLOT_NOT_IN_DISPATCH_SCOPE / demandId= / L2-SRJ-OUTSIDE-20261017T183348939Z / correlationId=0de0c231-142a-47d2-8056-aaba42edb702` |
| 范围外的拒收也是新的一条提交（新的 messageId），不是把上一条重发一遍 | PASS | `1 条新提交 / 与第一条不同` | `1 条 / 0de0c231-142a-47d2-8056-aaba42edb702` |
| 两次拒收之后仍然没有装货命令、没有仓位操作，旅程仍在等录入 | PASS | `0 / AwaitingSublot` | `0 / AwaitingSublot` |
| 重扫之后发的是装货命令：给的是这条需求与重扫那条子批号，仓位数是受理时冻结的值 | PASS | `3d25d1d0-0bb7-433f-9f89-a0e563259f13 / L2-SRJ-20261017T183348939Z / 1` | `3d25d1d0-0bb7-433f-9f89-a0e563259f13 / L2-SRJ-20261017T183348939Z / 1` |
| 被拒过的子批号重扫之后照常受理：旅程离开等录入，消费的是重扫那条新提交 | PASS | `ConsumedSublotMessageId=a65b663e-8990-4d95-988c-2e93c8d0ddc8（新提交）` | `ConsumedSublotMessageId=a65b663e-8990-4d95-988c-2e93c8d0ddc8 / 重扫提交=a65b663e-8990-4d95-988c-2e93c8d0ddc8` |
| 装载操作提交，且全程只有一条装货尝试 | PASS | `Committed / 1 条` | `Committed / 1` |
| 同一条提交只拒一次：三次录入对应两条拒收，被拒的正是前两条，受理的那条没有拒收 | PASS | `2 条拒收 / 3 条提交 / 被拒的是前两条` | `2 条拒收 / 3 条提交 / 被拒 bdf1adea-637f-4d43-8ef9-22cd2ebc8342, 0de0c231-142a-47d2-8056-aaba42edb702 / 受理 a65b663e-8990-4d95-988c-2e93c8d0ddc8` |
| 全程没有 SUBLOT_SUBMISSION_MISMATCH：被拒过的提交留在库里，之后每一轮都还会被读到，旅程的阻断原因始终为空 | PASS | `无阻断原因` | `BlockReasonCode=(null)` |
| 全程只建了一条 RIoT 单（取货），没有重复派车，也还没有关卡单 | PASS | `1` | `1 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
