# control-server#453：合成库 protocolCommit 断言的变异证明

## 结论

需求承载 G3 runner 走合成库时，库里记下的 `protocolCommit` 必须等于绑定协议。这条要求写成了
`protocolAndBuildIdentityBoundToTheSharedBinding` 的一个合取项。下面三轮证明它判得出来：

| 目录 | 库从哪来 | 库里的 `protocolCommit` | 结果 |
| --- | --- | --- | --- |
| `synthetic-green/` | 合成库，生成器未改 | `86575456…`（等于绑定） | `DEMAND_BEARING_G3_VECTORS_PASS`，16/16 |
| `mutation-red/` | 合成库，生成器被变异 | `1531489e…`（`protocol-v0.1.1`） | `DEMAND_BEARING_SLICE_FAIL`，**只红** `protocolAndBuildIdentityBoundToTheSharedBinding` |
| `field-path-control/` | 把变异那份库当外场库，经 `-FieldRunRoot` 恢复 | `1531489e…` | `DEMAND_BEARING_G3_VECTORS_PASS`；`exemption = TICKET_17_KNOWN_EXEMPTION_FIELD_STORE_HISTORY`、`protocolCommitAsserted = false` |

第三轮是对照：同一份库、同一个错的 `protocolCommit`，换成外场库路径就是绿的。这说明 `TICKET_17` 豁免
只在外场库路径上生效，第二轮的红来自合成库路径上新加的这一项，不是别的东西坏了。

**这三轮都不是门禁证据。**它们只证明断言写对了；正式的需求承载 G3 在集成分支顶端另跑一次。

## 身份

- runner：`a1095ca2322c6e50172051c6fad3e69445b20411`（本票实现提交），三轮起跑时工作区都干净。
- 绑定：用 `-SharedRunnerSource` 指向一份临时的 `run-staged-g3.ps1` 副本，只改 `$ControlServerCommit`，
  其余三项与仓库里的一致（onboard `4e40e196`、simulator `fb5f7c59`、protocol `86575456`）。
  - 第一、三轮 ControlServer 绑定 `a1095ca2`。
  - 第二轮 ControlServer 绑定 `01ed0100d939655d0bd90ddc57e0f21fcf0d9012`：本地一次性分支
    `scratch/cs453-mutation` 上的提交，在 `a1095ca2` 之上只改了生成场景一处——导出库之后执行
    `UPDATE SessionRecoveries SET ProtocolCommit = '1531489e42e328f28bfe0c51ed3f8c56e5ce0279'`。
    那个分支没有推送，验证后已删除。
- 恢复的库 SHA-256：第一轮 `b2cf2e9f…1349`；第二、三轮同一份 `4d7b2665…1ef8`。

## 精简

每轮只留 `run-result.json`、`configuration.json`、`slices/` 与生成器的 `assertions.json`。去掉的是服务端
控制台日志、探测报文记录与生成器的库快照，三轮合计约 5.7 MB，其中的结论都已在 `run-result.json` 里。
