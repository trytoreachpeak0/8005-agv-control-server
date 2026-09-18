# 缺陷：急停在每一轮评估都重发一次，发出去的急停从不被确认

Status: fixed
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs`、`src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs`）
Found by: 2026-09-13 批次 2 票 19（W1 空载急停演练）开工前调研的代码走查；随后由 [`emergency-stop-single-trigger` 在修复前代码上的运行](../../evidence/l2/20260913-b2close-emergency-stop-single-trigger-prefix-65bffc0c-001/SUMMARY.md) 复现
Product at discovery: `fp/v2-impl@65bffc0c60591648a11f7c389d5cbcd27ee103e1`
Fixed in: `fp/b2-close@dfdbe9a3`（场景自身的修正在 `0619623c`）
Peers: 合成车载端

**红在产品。修复前，票 19 现场演练的出口判据「8005 侧只发一次调用」做不到。**

## 现象

同一个新场景 `emergency-stop-single-trigger` 跑在修复前的代码上。车在动时单被报 FAILED，服务端升级、发出第一次
`triggerEmergency`，之后：

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| `L2-ES-05` 闩锁还没锁上、车仍在动，又评估了 4 轮 | FAIL | 1 行 / 1 次 | 5 行 / 5 次 |
| `L2-ES-06` 闩锁读不到，又评估了 4 轮 | FAIL | 1 行 / 1 次 | 9 行 / 9 次 |

接着闩锁锁上，场景等「发出它的那一行被改记为 Confirmed」，30 秒超时，最后读到的仍是 `Pending`。

也就是说：**一次急停在 8 轮评估里变成了 9 次真实调用，而且闩锁锁上以后也没有任何一行被确认。**现场的运行时
每两秒评估一次，RIoT 的闩锁在调用后约一秒才锁上（Round 19），所以只要有一次评估赶在锁上之前，或者一次回读
失败，现场就会记到第二次调用。

## 根因

两处，而且互相掩盖。

**一、重复请求停车不看退避。**故障协调器在证不出停住的每一轮都调 `EscalateAsync` → `RequestStopAsync`。
`RequestStopAsync` 只读一次闩锁：锁上了就不发，否则立刻发一次 `triggerEmergency`——不看这一轮急停是否已经发出，
也不看退避。`EscalateAsync` 的注释写的是「重复请求无害，只有到期才发」，那描述的是 `EvaluateAsync` 的行为，不是
它实际调用的方法。

**二、负责跟进的 `EmergencyStopSupervisor.EvaluateAsync` 在 `src/` 里没有调用方。**按退避重试、闩锁锁上后把
那一行记为 `Confirmed`、闩锁被外部解除时立即重触发（`REQ-0248`），全在这个方法里。票 10 把监督器设计成「被驱动、
不自己跑」，票 11 的协调器只驱动了 `RequestStopAsync` 这一半。

**为什么以前没发现。**票 18 的 `command-surface-order-hold` 让车停在站上，按 `REQ-0246` 不升级，场景断言的是
「零次急停」（`L2-CS-12`），这条路径一次都没走到发急停之后。单元测试里退避与确认都是直接调 `EvaluateAsync` 证的，
没有一条覆盖「协调器反复调 `RequestStopAsync`」这条真实的驱动方式。

## 修复

- `RequestStopAsync` 遇到同一代故障、仍未结束的急停时并入它，走和 `EvaluateAsync` 相同的规则：闩锁锁上就把
  那一行记为 `Confirmed`；没锁上或读不到就按退避等；锁被外部解除就立即重触发并告警。停车请求永远不发起解除
  （`REQ-0249` 的「恢复严」）。更新一代的故障仍发自己的急停，否则解除规则会一直拿旧代数去比新故障。
- 两处共用抽出来的 `AdvanceUnlatchedAsync`，保证「什么时候可以再发」只有一处规则。
- `VehicleFaultCoordinator` 在本轮不需要升级、但本代已经升级过时，驱动 `EvaluateAsync`。这里刚记录了故障，
  原因未清除，所以不会解除。

## 证据

| 运行 | 代码 | 结果 |
| --- | --- | --- |
| [`20260913-b2close-emergency-stop-single-trigger-prefix-65bffc0c-001`](../../evidence/l2/20260913-b2close-emergency-stop-single-trigger-prefix-65bffc0c-001/SUMMARY.md) | 修复前 `65bffc0c`，工作树里放着新场景与编排器改动的未提交副本 | FAIL，见上表 |
| [`20260913-b2close-emergency-stop-single-trigger-001`](../../evidence/l2/20260913-b2close-emergency-stop-single-trigger-001/SUMMARY.md) | `dfdbe9a3` | FAIL，**红在场景自己**：`Get-RiotInvocations` 用 `return @(...)`，零个元素时返回 `$null`，严格模式下取 `.Count` 抛错。修在 `0619623c` |
| [`20260913-b2close-emergency-stop-single-trigger-002`](../../evidence/l2/20260913-b2close-emergency-stop-single-trigger-002/SUMMARY.md) | `0619623c` | PASS，14 条判据 |
| [`20260913-b2close-command-surface-order-hold-001`](../../evidence/l2/20260913-b2close-command-surface-order-hold-001/SUMMARY.md) | `0619623c` | PASS，20 条判据；停在站上的车仍不被急停，`OrderHold` 仍只发一次 |

单元测试新增 8 条（监督器 6、协调器 2），修复前 5 条红、3 条防护性用例为绿；全量 `707 passed / 0 skipped`。
场景已挂进 `l2.yml`，连跑三次。

## 仍未解决，不在本缺陷范围内

**一张被报 FAILED 的单造成的故障，没有清除路径。**`REQ-0167` 要求 8005 自己触发的急停在「原原因消除」后自动解除，
而清除故障事实的唯一入口是修复续行 `ResumeAsync`，它要求原订单确认为 HELD；FAILED 的单永远不满足。
`REQ-0239` 后半句「原订单已明确终结、相关阻断收敛」怎么判、谁来判，需求没有定义，票 11、票 18 都把它记在
`REQ-0253` 权限模型那条线上。结果是：演练或真实故障之后，车会一直锁着；而在故障仍在时去 RIoT 里手动解锁，
服务端会按 `REQ-0248` 立即重新急停。现场说明见 `docs/emergency-stop-field-fallback.md` 末节。需产品负责人裁定。

**2026-09-15 补记（control-server#63）**：急停这一半已由需求变更 `CP-0003`（基线 `v1.3.0`）裁定并实现——急停锁住即视为
停稳（`REQ-0247` 修订），锁住后由服务端人员确认原因已消除、无货、仓门已关，服务端自己解除且不重触发（新增
`REQ-0356`，要求该车订单已终结）。车因此不再「一直锁着」。故障事实本身仍没有 FAILED 单的清除路径，派车阻断照旧，
这一半仍待裁定。上文所指的文档末节已按新条文重写为「急停之后车怎么回来」。
