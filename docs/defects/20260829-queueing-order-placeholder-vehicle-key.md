# 缺陷：把 QUEUEING 订单的 `"--"` 占位符当成已绑定车辆键

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: [`授权单次真实建单`](../../evidence/g3/20260829-authorized-single-real-create/SUMMARY.md)
Product at discovery: `ControlServer_MVP@9a42582654d7ca793499556522beb1547403fafb`
Fixed in: `ControlServer_MVP@addc2fab11d506b19d9a94c83537ed24d4233ef8`

## 现象

首次真实建单成功、车辆也真的从关卡开到了取货站并使订单进入 `orderState=5 SUCCESS`，但旅程
停在 `Stage=AwaitingPickupArrival` / `BlockReasonCode=PICKUP_TerminalReconciliationRequired`，
intent 为 `TERMINAL_RECONCILIATION_REQUIRED`，无法继续。

审计链显示建单后紧接着的那次对账被判为 `UNKNOWN`：

| Sequence | Phase | Outcome |
| --- | --- | --- |
| 4 | `CREATE_RESPONSE` | `ACCEPTED` |
| 5 | `POST_CREATE_RECONCILIATION` | **`UNKNOWN`** |
| 6 | `POST_CREATE_RECONCILIATION` | `TERMINAL` |

## 根因

RIoT 在尚未给订单绑定车辆时，`executeVehicleKey` 返回字面量 `"--"`。这有两条 `OBSERVED`
契约记载：BC-ORDER-012（不可达目的站时「保持 `QUEUEING`/`execute=--`」）与 BC-ORDER-013
第 2、4 条（「QUEUEING 单的 `executeVehicleKey` 常为 `"--"`」「业务侧应按
`appointVehicleKey == 本车 || executeVehicleKey == 本车` 做客户端过滤」）。

`HttpRiotMovementGateway.ToObservation` 原本只在该字段为 null 或空白时才回退到
`appointVehicleKey`：

```csharp
string? vehicleKey = string.IsNullOrWhiteSpace(order.ExecuteVehicleKey)
    ? order.AppointVehicleKey
    : order.ExecuteVehicleKey;
```

`"--"` 既非 null 也非空白，于是被原样当作车辆键向下传递。`MatchesFrozenIntent` 要求
`observation.VehicleKey == intent.VehicleKey`，`"--"` 与 `BROKERX-…` 不等，`ConfirmAsync`
遂转入 `MarkUnknownAsync`，intent 在自己建单成功数秒后被打成 `RESULT_UNKNOWN`。

`RESULT_UNKNOWN` 的 intent 再也回不到 `CONFIRMED`；而 `EnsureMovementConfirmedAsync` 只在
`Confirmed` 时放行，因此其后的 `IsTrustedArrivalAsync` 永远得不到执行机会。

**到站判定本身没有问题**：它一直要求 `Kind == Terminal && OrderState == 5`，与成功语义一致；
它缺的只是一个已确认的 leg。此前把该现象归因为「`5 SUCCESS` 被误并入 Terminal」或「1 秒
竞态」并不准确，特此更正。

## 修复

1. 新增 `IsAssignedVehicleKey`：只有当 `executeVehicleKey` 非空白**且不是 `"--"` 占位符**时
   才视为已绑定，否则回退到 `appointVehicleKey`。这正是 BC-ORDER-013 第 4 条的规则；订单一旦
   进入执行，真实 `executeVehicleKey` 仍然优先，安全校验不受削弱。
2. 把 `orderState == 5` 从「终态需人工对账」分支中拆出并按已确认处理。它与 `2 CANCELLED`、
   `4 FAILED`、`6 DELETED` 共用 `Terminal` 种类，而后者确实需要人工介入；不拆分则「订单在
   首次 post-create 对账之前就完成」的 leg（例如车辆本就停在目标站）同样会死在终态。物理
   到站仍由 `IsTrustedArrivalAsync` 独立证明——要求车辆停在目标站、地图一致、无活动订单。

两处原本用 `orderState 5` 表示「终态」的测试夹具改用 `2 CANCELLED`，以继续守住人工对账路径。

## 门禁

Release 构建 0 warning / 0 error，`dotnet format` PASS，测试 **218/218、0 skip**；八片 G2
全部绑定 `addc2fa` PASS，集合 SHA-256
`ad49671d9015af69f379da74d0c32292826db1efacfb088f55c42c04e93f8cb4`；package manifest
`db0d1f91433f25fe0b647bccd20565343d9fb90106c61ba75c42725244fe37d2` 已可回滚部署到本机。

新增单元测试直接覆盖占位符：`"--"`、`" -- "`、null、空串、纯空白五种取值均回退到
`appointVehicleKey`；另有一条断言订单进入执行后 `executeVehicleKey` 优先。

## 尚未验证的部分

`"--"` 解析本身已由单元测试覆盖，但「建单 → `CONFIRMED` → 到站 → `AwaitingSublot`」这条完整
链路只能靠一次新的真实建单验证，那会再次移动车辆，须另行取得现场物理安全 GO 与逐次授权。
车辆当前已由用户开回关卡（站点 210，空载，电量 34%）。
