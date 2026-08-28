# 受理门禁修复验证：出厂车载配置下完成受理并停在建单开关

## 运行类型

`ZERO_MUTATION_PEER_REHEARSAL_NOT_G3`。验证
[`docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md`](../../docs/defects/20260829-intake-gates-on-unspecified-onboard-facts.md)
中 D-1／D-2 的修复。**本次未对车载配置做任何覆盖**，`supportsBatchUnlock` 保持出厂的
`false`。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@9a42582654d7ca793499556522beb1547403fafb`
- package manifest SHA-256：`e40c1865e9e6753d58b3cc541dee074cfc55f38d069d9d2194e7b917e0ffdc28`
- OnboardHmi：`84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`（一次性克隆，配置未改）
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议：`protocol-v0.1.1@1531489`，manifest `a467c0c4…6389f`
- MesIngest：本机 v2.4；RIoT：`http://172.19.206.222:8888`

## 修复内容

**D-1**：不再读取 `supportsBatchUnlock`。协议对该字段无任何语义定义、其自身合法样例取值为
`false`，车载亦无行为分支；"车辆能否操作这批仓位"在下发命令时已按 `AvailableSlots` 判定。

**D-2**：`MaximumEvidenceAge` 不再作用于两个快照的 payload 时间，改为作用于**该会话代次最后
一条入站消息的服务端接收时间**。协议未规定快照节奏，车载每会话只发一次，原做法把受理窗口
压成 30 秒；而 `CurrentReadySessionAsync` 只读持久化的 Ready 行、本身没有活性成分，因此死掉
的对端会永远显示 departure-safe。以入站接收时间为准既保留了活性下界，又不受对端时钟影响；
任何入站消息均计入，覆盖首个 Heartbeat 之前的窗口。`MaximumEvidenceAge` 继续单独约束真正
轮询得到的 RIoT 车辆观测。

修复过程中引入并已修正一个自身缺陷：活性扫描遍历全部入站行，会遇到 `sessionGeneration` 为
`null` 的 `SessionHello` 信封并抛出，导致每次旅程迭代 fail-closed——现场表现为"对端连上后
再也不被轮询"。现已按 token 类型判定并跳过，且新增回归测试覆盖。

## 门禁结果

- Release 构建 0 warning / 0 error，`dotnet format` PASS
- 完整测试 **211/211 PASS，0 skipped**
- 正式 `protocol-v0.1.1` 下 W2G-IS-00～07 八份 G2 全部绑定 `9a42582` PASS
- 八份 `gate-result.json` 集合 SHA-256：
  `4bf7ba9c9b7cc3131d2dfe76e6012c0bd55cd390de46a09d2d446642a2c065d7`
- 可回滚本机部署 PASS，已安装 `sourceCommit=9a42582`，双层 runtime=false、建单开关=false

测试变更：`onboard-stale` 场景改为 `onboard-silent`，改为回拨接收时间而非 payload 时间，
从而守住替代规则；新增三条覆盖——活会话中较早的快照仍可受理、`supportsBatchUnlock=false`
不阻断受理、无会话代次的入站信封不破坏活性判定。

## 运行验证（出厂车载配置）

观察 150 秒。WIRE_TO_GATE 判定：

| ReasonCode | 条数 |
| --- | --- |
| `OUT_OF_SCOPE_AREA` | 8 |
| `ELIGIBLE` | 6 |
| `ACCEPTED` | 1 |
| `PACKAGE_CAPACITY_NOT_UNIQUE` | 1 |
| `AREA_STATION_NOT_FOUND` | 1 |

修复前同一配置下受理数为 0，全部判 `ONBOARD_FACTS_NOT_READY`。

旅程推进到 `Stage=AwaitingPickupArrival`，稳定停在
`BlockReasonCode = PICKUP_CreateDispatchDisabled`，最后一次更新时间为运行开始后约 2 分 35
秒——远超原先 30 秒的受理窗口，证明 D-2 已解除。

建单资格保全（关闭态）：

| 字段 | 值 |
| --- | --- |
| `Status` | `PENDING_RECONCILIATION` |
| `CreateAttemptCount` | 0 |
| `DispatchAuditVersion` / `DispatchAuditSequence` | 1 / 0 |
| `RiotDispatchAuditEvents` 总行数 | 0 |

stderr 为空，58105／58107／1502／58006 四端口全部回收，无 RIoT mutation、无订单、无车辆移动。

## 未证明的事项

本次仍是关闭态演练，不构成任何切片的 G3。真实建单、移动闭环、现场物理安全确认与逐次授权均
未进行。`PACKAGE_CAPACITY_NOT_UNIQUE` 那条仍需具名业务负责人提供精确 boxes-per-basket。
W2G-IS-00～07 的正式 G3 与 RC 保持 `INCONCLUSIVE`。
