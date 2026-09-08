# 票 09：B2 多车执行的验收证据

## 运行类型

本机 tier 1 ＋ **七条 L2 合成场景全跑**。**不动车、不使用任何现场凭据、不碰真 RIoT、不需要
桌面**（`real-onboard-*` 三条不在本票范围）。

## 结论

| 项 | 结果 |
| --- | --- |
| 新增测试 | **23 条**（`MultiVehicleExecutionTests`） |
| 全量套件 | **557 passed / 0 failed / 0 skipped**（票 11 基准 534 ＋ 23） |
| 新增 migration | **零**。`Persistence/Migrations/` 与 `ControlServerDbContext` 一行未动 |
| `Ports.cs` 改动 | **零**。票 06 已把 `IVehicleDispatchPolicyStore` 定齐 |
| L2 合成场景 | **七条全 PASS**（见下表） |

原文：[`multi-vehicle-tests.txt`](multi-vehicle-tests.txt)（23 条逐条）、
[`full-suite.txt`](full-suite.txt)（全量）。

## L2 七条

| 场景 | 结果 | 证据 |
| --- | --- | --- |
| `three-synthetic-peers` | **PASS** | `../../l2/20260909-ticket09-three-synthetic-peers-002/` |
| `normal-load` | PASS | `../../l2/20260909-ticket09-normal-load-001/` |
| `session-established-while-moving` | PASS | `../../l2/20260909-ticket09-session-established-while-moving-001/` |
| `load-result-requires-recovery` | PASS | `../../l2/20260909-ticket09-load-result-requires-recovery-001/` |
| `create-gate` | PASS | `../../l2/20260909-ticket09-create-gate-001/` |
| `create-gate-unapproved` | PASS | `../../l2/20260909-ticket09-create-gate-unapproved-001/` |
| `route-graph-engine` | PASS | `../../l2/20260909-ticket09-route-graph-engine-001/` |
| `load-command-never-answered` | PASS | `../../l2/20260909-ticket09-load-command-never-answered-001/` |

`three-synthetic-peers` 是本票唯一一条**断言方向被翻过来**的场景，其余六条是回归。

`...-three-synthetic-peers-001` 是同一条场景的**失败留档**，故意保留：它证明断言先红后绿。
红的原因不是产品——三条会话当时已经都在——而是场景脚本自己把 `Invoke-L2Query` 的结果多包了
一层 `@()`，见下。

## 三车会话：断言从「只有第一台」翻成「三台都有」

`three-synthetic-peers` 在票 09 之前钉的是一个**已知的坏边界**，而且它在自己的注释里写明了
将来该怎么改：

> 今天只会有第一台的那一行——这条断言写的是当前边界，不是期望的终态。票 09 让三条会话同时
> 活之后，这里应当变成三行，那时这条会红，而它红得对。

现在：

- `setup.psd1` 里后两台的 `WaitForReady = $false` 去掉了，三台都等 READY，**三台都到了**。
- `L2-3P-06` 从 `AGV-FAKE-001` 改成三台，PASS。
- `L2-3P-07` 从「第一台是 Ready」改成**逐台断言**，三条全 PASS。逐台而不是数行数，是因为
  「接住了但没握完手」与「没接住」要人做的事不一样，合并成一条就看不出是哪台。

## 一条脚本侧的坑，值得单独记

`three-synthetic-peers-001` 那次失败里，三条会话的 `AgvId` 读出来长这样：

```
"expected": "AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003",
"actual":   "AGV-FAKE-001 AGV-FAKE-002 AGV-FAKE-003"
```

逗号变空格，而且逐台查 `Where-Object` 全部「缺行」。原因是 `Invoke-L2Query` 以
`return , $rows` 返回整张结果集，调用处再包一层 `@(...)` 得到的是**一个元素、那个元素是三行
的数组**；`$_.AgvId` 于是走 PowerShell 的成员展开，返回三个值的数组，`[string]` 它就得到用
`$OFS` 拼起来的那一行。

```powershell
function f { return ,@(1,2) }
@(f).Count      # 1  ← 包一层
$x = f; @($x).Count   # 2  ← 先赋值
```

**单行结果时这个错完全看不出来**：一个元素的数组展开成一个值，串起来就是那个值。所以它一直
潜伏到这条断言从一行改成三行的那一刻。仓里其他场景都是直接赋值（`$rows = Invoke-L2Query ...`），
本条已改成一致写法。

## 23 条 L1 覆盖了什么

| 组 | 条数 | 钉住的事 |
| --- | --- | --- |
| 会话隔离 | 4 | 三车各自成单；一台掉线不影响其余；一台的安全快照不放行另一台；同车两旅程仍冲突、异车两旅程不冲突 |
| 单 worker 按车串行 | 2 | 一轮只读一次目录做决策、车辆读不交错；一轮内前车拿走的需求对后车立即消失 |
| 每车超时预算 | 2 | 一台车挂住只丢自己这一轮；下一轮照常被服务 |
| `MT_WAIT_FOR_CHECKPOINT` | 3 | 预算内命名、超预算改名、走了就清；**且它仍读成 `Unknown`** |
| 两条判据 | 6 | 车不在策略里／任务类型不许／允许集为空／区无车／车不在区里／正常放行 |
| 策略与名册 | 2 | 配置写进三张表且按内容指纹幂等；空 `Fleet` 就是原来那台车 |
| 占用唯一性 | 1 | 三台车各持一个未释放占用；同车第二个在途订单被索引拒绝 |
| N 会话 | 3 | 按信封 `agvId` 路由；无会话的车报错不广播；同车第二条连接仍被拒 |

## 负例的比例

23 条里 **12 条断言的是「拒绝」并且断言拒绝报出的具体原因码**。两条新判据每一种拒绝都有自己
的原因码，测试按名字断言而不是按「没派出去」断言——一次因为错误原因发生的拒绝能通过后者，
而它会把人派到错误的地方去（与票 11 同一条理由）。
