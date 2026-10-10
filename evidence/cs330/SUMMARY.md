# control-server#330 证据汇总

我们车上运行中的外来订单：认出即在 RIoT 取消一次，回查确认明确终结之前照样阻断（需求基线 `v1.5.0` 修订后的 REQ-0148、REQ-0164）。

全部在假 RIoT（L1 夹具 `RecordingRiot`）上验证。**没有任何一次运行连过真 RIoT。**

## 红

| 文件 | 在哪个提交上跑 | 说明 |
| --- | --- | --- |
| `red/l1-01-f5c4c0cb.txt`（`.summary.txt` 是每条红的第一条报错） | `f5c4c0cb`（测试提交，监管器为空实现） | 110 条里 40 条红。红的原因都是「监管器还没做事」：列单读了 0 次、没有记录、没有取消、没有挡车、看板为空；另一条是切片账本里 `HttpRiotMovementGatewayTests` 的计数（23 → 26，新增的三条网关用例），随实现提交改账本 |
| `red/l1-02-cbe0434e.txt` | `cbe0434e`（测试提交） | 两次读之间订单换到另一辆我们的车：修前记成 `LEFT_VEHICLE`、新车没被挡。`Expected: ("DETECTED", "AGV-8005-SECOND", …)`，`Actual: ("LEFT_VEHICLE", <原车>, …)`。修在 `9f7b3ccb` |

`d3790f6f` 补的两条护栏用例（武装后崩溃不再发、只有明确终结才放车）写在实现之后，判别力由下面的变异 M15、M16、M19 给出。

## 变异反向验证

`red/l1-mutations/run-mutations.py` 每次拿掉一条护栏或一个判断，重建，跑相关测试，记录变红的用例，再从备份还原并核对哈希。替换文本必须恰好出现一次，构建必须 `0 Error(s)`，否则整轮停下。跑了两轮：

| 轮次 | 在哪个提交上跑 | 变异 | 测试范围 | 位置 |
| --- | --- | --- | --- | --- |
| 第一轮 | `9f7b3ccb` | M01～M28 | `ForeignRunningOrderTests`、`BlockedJourneyDashboardTests`、`PickupDispatchPlanPastOwnOrderTests`、车队夹具的途中追加用例（119 条） | `round1/summary.txt`、`round1/M01.log`～`M28.log` |
| 第二轮（独立审查修改之后） | `1b341afd` | M01～M35（新增 M29～M35） | 上面四处加 `OwnOrderRebuildTests`（165 条） | `summary.txt`、`M01.log`～`M35.log` |

下面按第二轮说。35 条里 32 条有用例变红。

票上点名的两个方向：

- **外来单被当成自己的而放行**（M01，所有在跑的单都当自己的）：21 个用例变红，包括取消、0/1 门禁、#314 放行、看板、取消开关。
- **自己的单被当成外来单**（M02，拿掉 `OrderIntent` 判据）：9 个用例变红。本服务端自己的在途单（`W2G-` 开头）这时没有被取消，而是落到「认不准、只挡不取消」，upperId 前缀在这里是第二道防线；只靠意图认出的那张（upperId 不像自己的）被取消了，所以 `by-upper-id` 那格红。**审查 S2 要求的前提钉在这里生效**：`OwnOrderRebuildTests` 里加了断言的两条（取货单重建、GATE 腿及其重建）都变红——自己的单一旦失去意图依据，监管就给它记一行。

审查修改新增的七条：

| 变异 | 拿掉了什么 | 变红的用例 |
| --- | --- | --- |
| M29 | 取消开关不起作用（关着也发取消） | 开关关着那条；「落不了定」没被授权那一格 |
| M30 | 开关打开后，因开关关着而挡着的单不再被接着取消 | 开关关着那条（打开后那一段） |
| M31 | `HELD_CANCEL_NOT_AUTHORIZED` 不挡车 | 开关关着那条、0/1 门禁「没被授权」那一格、看板 |
| M32 | 离开运行列表又读不到终结的单永不转人工 | 「落不了定」四格 |
| M33 | `UNSETTLED` 不挡车 | 「落不了定」、看板、车队夹具的途中追加用例（见下） |
| M34 | 终结时不改写取消结果 | 「取消后仍在运行转人工」三格（最后断言 `CANCELLED`） |
| M35 | 阻断卡片不加指向外来单的那句 | 「外来单不算作本车在途单」两格 |

M33 让车队夹具的途中追加用例也红了，要解释一下：那条用例直接往库里写一行 `STILL_RUNNING_AFTER_CANCEL`，`LastSeenRunningAt` 是纪元时刻；那个夹具的 RIoT 列单为空、按单号也读不到，第一轮监管就按审查 S1 的规则把它改成 `UNSETTLED`，照样挡车。所以那条用例守的是「挡车状态」这一族，拿掉 `UNSETTLED` 它就红，是对的。用例注释已照此改写（`1b341afd` 之后的提交，只改注释）。

没有变红的三条，与第一轮相同，都是预先写明「预期被另一道护栏兜住」的：

| 变异 | 拿掉了什么 | 为什么不红 |
| --- | --- | --- |
| M09 | 执行车辆是 `--` 占位符时不算在跑 | `--` 本来就不在我们车的 `deviceKey` 名册里，按名册认车那一步照样挡住 |
| M14 | 发前再读没读全时照样往下走 | 没读全时列单是空的，订单就按「不在列单里」处理，要按 orderId 回查到明确终结才动，不会发取消 |
| M20 | 列单没读全时照样判 | 同上：从没读全的列单里什么也认不出；已挡着的记录要回查到明确终结才放 |

## 绿

- `green/l1-9f7b3ccb.txt`：本票相关的十个测试类，346 条全部通过（审查之前）。
- `green/l1-1b341afd.txt`：独立审查修改之后，相关的十一个测试类（加了 `OwnOrderRebuildTests`、`VehicleFaultRecoveryTests`、`RiotCreateDispatchGateOptionsTests`、`WaitingJourneyDashboardTests`），392 条全部通过。
- 并行实例自检 `scripts/parallel/Test-ParallelInstance.ps1`：218 条全部通过，其中 9 条是新开关的（缺节、没写 `enabled`、写成字符串、打开却没传开关；传了开关接受、另一个开关打不开它、传了开关仍拒 agv01、随包定义里是关的、键名对得上 C# 选项）。
- CI 全量测试 run [35862118890](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35862118890)（`59e145e2`，手动触发）：`已通过! - 失败: 0，通过: 2703，已跳过: 1，总计: 2704`；ready 之后一轮（`a22f0c03`）：test run [35865760658](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35865760658) 通过 2711。审查修改之后的一轮见 PR 正文「进度」。
