# 占位符修复验证：intent 首次达到 `CONFIRMED`，且未建第二单

## 运行类型

`AUTHORIZED_SINGLE_REAL_CREATE`，建单开关打开。用户为车辆充电并派了一次 RIoT 移动订单，使
`movementState` 回到 `MT_FINISHED`、安全投影恢复 `STOPPED`（reason codes 空）、电量 41%
`NO_CHARGE`，随后授权本次运行。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@addc2fab11d506b19d9a94c83537ed24d4233ef8`
- package manifest SHA-256：`db0d1f91433f25fe0b647bccd20565343d9fb90106c61ba75c42725244fe37d2`
- OnboardHmi `84b7f3f`（配置未覆盖）、slots-simulator `fb5f7c5`、协议 `protocol-v0.1.1@1531489`
- 隔离运行：全新 SQLite、全新 journal、临时端口 58105／58107

## 结果：修复生效

会话到达 `Ready`，`/health/ready` 200。受理一条真实 WIRE_TO_GATE Demand（另有 4 条
`ELIGIBLE` 受单车排他租约约束未受理），并建立车辆租约。

`OrderIntent` 状态：

| 字段 | 值 |
| --- | --- |
| `UpperId` | `W2G-94993971b3624edf81bc712d160e444a-PICKUP-1` |
| `Status` | **`CONFIRMED`** |
| `OrderId` | `order-2093389168079142912` |
| `CreateAttemptCount` | **0** |

审计链只有一行：

| Sequence | Phase | Outcome | ReturnedOrderId |
| --- | --- | --- | --- |
| 1 | `PRE_CREATE_RECONCILIATION` | **`CONFIRMED`** | `order-2093389168079142912` |

两点意义：

1. **intent 首次达到 `CONFIRMED`**。修复前，建单后紧接的对账必因 `"--"` 占位符失配而把 intent
   打成 `RESULT_UNKNOWN`，此后永不可能确认。本次 `PRE_CREATE_RECONCILIATION` 直接判定
   `CONFIRMED`，正是 `orderState 5 → 已确认` 与占位符回退两处修复共同生效的结果。
2. **未建第二单**。`upperId` 由 demand 决定性派生，全新数据库重建同一 intent 后，对账命中了
   首次运行遗留的同一订单并直接确认，`CreateAttemptCount` 为 0。BC-ORDER-004 的 upperId 幂等
   与新的确认路径共同保证了这一点。本次运行对 RIoT **零 mutation**，车辆全程未移动（仍在
   站点 210，电量 41%→40% 为静置自耗）。

`JourneyRuntime` 停在 `Stage=AwaitingPickupArrival`，`BlockReasonCode` 为 **null**——不是被
阻断，而是 `EnsureMovementConfirmedAsync` 已放行、`IsTrustedArrivalAsync` 正确地拒绝宣告到站，
因为车辆此刻在站点 210，而该订单的目标站是 `N2-5_N3-5`（站点 21）。

stderr 为空，58105／58107／1502／58006 四端口全部回收。

## 仍未验证的一跳

`CONFIRMED → 到站 → AwaitingSublot` 这最后一跳尚未走通，原因是环境状态而非代码：该 Demand 的
取货订单早在首次运行时就已 `SUCCESS`，但车辆随后被人工开回 210，因此「订单已完成」与「车辆
在目标站」无法同时成立。ControlServer 的两个判断都正确——leg 已确认故不再派单，车辆不在目标
站故不宣告到站。

验证该跳有两条路：

- **甲**：由 RIoT 侧把车辆移到站点 21，再重跑。届时订单 `SUCCESS` 与车辆在目标站同时成立，
  应推进到 `AwaitingSublot`。此路 ControlServer 不新建任何订单、不发起任何移动。
- **乙**：换一条尚无订单的 Demand，让 ControlServer 自行建单并驱动车辆走完全程。需要该
  Demand 被选中，而 `upperId` 的决定性派生会使全新数据库再次选中同一条。

甲更省、更安全，且恰好只隔离出这一段未测代码。

W2G-IS-00～07 的正式 G3 与 RC 保持 `INCONCLUSIVE`。
