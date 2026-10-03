# control-server#453：需求承载 G3 用合成库的自检运行与变异证明

## 结论

需求承载 G3 runner 改成当场生成合成库之后，在本票分支顶端 `b660f80e` 上跑通：
`DEMAND_BEARING_G3_VECTORS_PASS`，16/16。

**这不是出口证据。**三轮都用 `-SelfCheckControlServerCommit` 覆盖了共享绑定，
`commits.controlServerCommitSource = SELF_CHECK_OVERRIDE`。共享绑定按调度意见不在本票移动，仍是
`8d0a644e`，那个提交里没有生成场景。正式的需求承载 G3 结论要等出口票（cs#393）第 1 步把绑定移到包含
本票的提交之后再跑。**`classification` 与 `gate-result.json` 里的 `formalSlicePass: true` 在这三轮里不作数**：
分级函数不看 `controlServerCommitSource`，这一点没有在本票改动，已报调度。

**证据比以前弱一档。**以前恢复的是 2026-08-29 `agv01` 真车、真 RIoT 运行留下的库（`fullloop-20260829T131549Z`，
已丢失）。现在的库由被测构建在合成装置上写出：合成车载端、假 RIoT、假 MesIngest，没有真车，也没有真 RIoT。
16 条断言的面不变，但 `FP-IS-04`／`05` 的 G3 结论里不再包含「在真车写下的状态上成立」这层意思。
`fieldStoreProvenance.riotCreateAuditHistory` 那一节记录的是假 RIoT 的行为：假 RIoT 对从未建过的 `upperId`
的应答与真 RIoT 不同（`preCreateReconciliationObservedUnknownOnEveryLeg = false`），这一节本来就只记录、不断言
（cs#60），这里也不能当作关于 RIoT 的证据。

## 三轮

| 目录 | 覆盖的 ControlServer 提交 | 库从哪来 | 库里的 `protocolCommit` | 结果 |
| --- | --- | --- | --- | --- |
| `selfcheck-pass/` | `b660f80e`（本票分支顶端） | 合成库 | `86575456…`（等于绑定） | `DEMAND_BEARING_G3_VECTORS_PASS`，16/16 |
| `mutation-red/` | `01ed0100`（变异） | 合成库 | `1531489e…`（`protocol-v0.1.1`） | `DEMAND_BEARING_SLICE_FAIL`，**只红** `protocolAndBuildIdentityBoundToTheSharedBinding` |
| `field-path-control/` | `b660f80e` | 第二轮那份变异库，经 `-FieldRunRoot` 恢复 | `1531489e…` | `DEMAND_BEARING_G3_VECTORS_PASS`，`TICKET_17` 豁免生效 |

每轮的 `run-result.json` 里，`assertionDetails.protocolAndBuildIdentityBoundToTheSharedBinding` 写明了第七合取项这一轮
查没查：

- 第一轮：`SYNTHETIC_RIG: seventh conjunct in force; … -> equal`
- 第二轮：`SYNTHETIC_RIG: seventh conjunct in force; … -> NOT equal`
- 第三轮：`FIELD_RUN: seventh conjunct exempt (TICKET_17); … recorded, not asserted`

第三轮是对照：同一份库、同一个错的 `protocolCommit`，换走外场库路径就是绿的。这说明豁免只在外场库路径上生效，
第二轮的红来自合成库路径上的这一项，不是别的东西坏了。

## 身份

- runner：`b660f80eb02619056c13ea8def21e5d90ac7dbb2`，三轮起跑时工作区都干净。开跑前实读 `fp/v2-impl` 顶端为
  `46148e35`，本分支包含它。
- 共享绑定（未动）：onboard `4e40e196`、simulator `fb5f7c59`、protocol `86575456`。ControlServer 由覆盖参数给出，
  见上表。
- `01ed0100d939655d0bd90ddc57e0f21fcf0d9012` 是本地一次性分支 `scratch/cs453-mutation` 上的提交，在 `a1095ca2` 之上
  只改了生成场景一处：导出库之后执行
  `UPDATE SessionRecoveries SET ProtocolCommit = '1531489e42e328f28bfe0c51ed3f8c56e5ce0279'`。生成场景在
  `a1095ca2` 与 `b660f80e` 之间没有改动。那个分支没有推送，验证后删除。
- 恢复的库 SHA-256：第一轮 `8da7e9ce547b21f8…`；第二、三轮同一份 `a071397251ec915e…`。
- 第一轮时间：2026-10-03 04:26:12–04:29:13 UTC，用的是调度放给本票的本机时段，跑完即交回。第二、三轮不占时段。

## 精简

每轮保留 `run-result.json`、`configuration.json`、`slices/`、探测结果、重启后握手与探测报文记录；生成器保留
`assertions.json`、`SUMMARY.md`、`timeline.jsonl`。去掉的是服务端与各组件的控制台日志、生成器的库表快照，每轮约
1.8 MB。其中的结论都已在 `run-result.json` 里。runner 自己的密钥扫描三轮都是 0。

同一 PR 早先提交过一份变异证明（`20261003-cs453-protocol-commit-mutation/`，runner `a1095ca2`，经 `-SharedRunnerSource`
临时副本覆盖绑定）。那三轮的结论与这里一致，但证据里没有覆盖标记，也没有 `assertionDetails`，已由本目录取代，
内容留在 git 历史里。
