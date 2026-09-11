# 现场窗口一证据：三个操作员不作为场景在车上的 slots-simulator 上无人驱动验收

**注意：本次按 `-MinimumHoldMinutes 3` 判场景 C，低于现场要求的 20 分钟，只能算彩排，不能当现场窗口的证据。**

结论：**PASS**

对应 [ADR-cross-0058](../../../8005-agv-program/docs/adr/cross/0058-slot-convergence.md) 的决策 1、2、4、5，
以及地图票 [现场窗口一（无人）](https://github.com/trytoreachpeak0/8005-agv-program/issues/45)。

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T143741337Z` |
| 窗口 | `FW-SC1` 装卸站收敛语义 |
| 现场 | L2 真装置彩排（本机） |
| 现场人员 | 无人到场，FieldOperator.psm1 run 20260911T143252185Z |
| agvId | `AGV-L2-001` |
| IO | `127.0.0.1:58412`（SIMULATOR） |
| 场景 C 等待下限 | 3 分钟 |
| 恢复窗口 | 窗口内开启 |
| 数据库 | `local-snapshot` |

**这个窗口号与 `evidence/field/README.md` 里的 `W1`/`W2`/`W3` 不是一回事**：那三个是仓位配置就绪
那条线的窗口（车辆资格 / 多车与等待点 / 自动充电）。这里的 `FW-SC1` 是装卸站收敛语义的第一次现场窗口。

## 判据

| 判据 | 说明 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- | --- |
| SC1-A-01 | 开门不放料：车载端自动重发开锁脉冲，轮次不设上限（现场至少走到第 3 轮） | PASS | `UNLOCKING >= 3` | `3` |
| SC1-A-02 | 重开几轮之后照常放料，装载提交——不设上限的重开有出口 | PASS | `Committed` | `Committed` |
| SC1-A-03 | 人没放料没有被判成失败：这个 attempt 上没有 FAILED 结果 | PASS | `0` | `0` |
| SC1-A-04 | 需求没有被判 RecoveryRequired | PASS | `不是 RecoveryRequired` | `Accepted` |
| SC1-A-05 | HMI 上没有出现恢复入口——操作员迟疑不需要管理员凭据（驱动脚本读车载端快照 availableRecoveryActions 里的三个恢复动作） | PASS | `False` | `False` |
| SC1-B-01 | 车载端报 FAILED | PASS | `FAILED` | `FAILED` |
| SC1-B-02 | 三个物理字段都是明确的：State 不是 Unknown、锁已闭、开锁输出已复位 | PASS | `三字段齐全且明确` | `slot2:State=Empty/DoorLocked=True/UnlockOutputReset=True` |
| SC1-B-03 | 服务端走确定失败而不是 RecoveryRequired | PASS | `Failed` | `Failed` |
| SC1-B-04 | 确定失败之后服务端自己终结这条需求：Cancelled、按 CANCELLED_BY_STATION_TIMEOUT 永久抑制、装货命令已结算 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled` |
| SC1-B-06 | 确定失败之后旅程自己离开这一格，不需要任何人按任何按钮 | PASS | `不是 2/AwaitingLoadResult` | `9/AwaitingGateArrival` |
| SC1-B-05 | 结算之后会话仍在 Ready：确定失败是业务结果，不是会话故障 | PASS | `Ready` | `Ready / READY` |
| SC1-C-01 | 站点期限到期时告警挂上了 STATION_TIMEOUT_DOOR_NOT_CLOSED | PASS | `STATION_TIMEOUT_DOOR_NOT_CLOSED` | `STATION_TIMEOUT_DOOR_NOT_CLOSED` |
| SC1-C-02 | 期限到期时停靠没有被关闭，stage 停在 AwaitingLoadResult 而不是 Blocked | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| SC1-C-03 | 期限到期后再等 3 分钟依然不结束——等待不会自己退化成结束 | PASS | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED / Prepared` | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED / Prepared` |
| SC1-C-04 | 那次复查确实在期限之后 3 分钟以上 | PASS | `>= 3 分钟` | `3.0 分钟` |
| SC1-C-06 | 门一直开着：晾过 OperationTimeout 之后提示还在走而脉冲只打过一次——提示节拍不重复开一把已经开着的锁 | PASS | `UNLOCKING = 1 / WAITING_OPERATOR >= 2` | `UNLOCKING 1 / WAITING_OPERATOR 2` |
| SC1-C-05 | 关门之后按真实 IO 读数结算（决策 1 会先重开一轮），告警随之消失 | PASS | `告警清空 / 操作已结算` | `AwaitingGateArrival / - / Failed` |
| SC1-W-01 | 三个场景都有现场记录，且记录由驱动脚本按实际动作写出（模拟器 IO 下无人到场，照片不适用） | PASS | `3 个场景 / drivenBy 非空` | `3 个场景 / drivenBy=FieldOperator.psm1 run 20260911T143252185Z` |
| SC1-W-02 | 窗口内恢复入口是开着的——否则决策 2 只验证了一半 | PASS | `True` | `True` |
| SC1-W-03 | 每一个 checkpoint 上会话都停在 Ready——全程没有把开着的仓门当成会话故障 | PASS | `每个 checkpoint 都是 Ready` | `01-00-ready=Ready; 02-c-deadline-reached=Ready; 03-c-plus-hold=Ready; 04-b-settled=Ready; 05-finalize=Ready` |

## 实测事实

不作判据，但下一次窗口和现场归因都要用：

| 项 | 值 |
| --- | --- |
| `ioKind` | `SIMULATOR` |
| `minimumHoldMinutes` | `3` |
| `drivenBy` | `FieldOperator.psm1 run 20260911T143252185Z` |
| `A.slotOperationAttemptId` | `f741c22c-add9-4e55-a0e7-8cce2a3026fa` |
| `A.phaseCounts` | `UNLOCKING=3 WAITING_OPERATOR=3` |
| `B.slotOperationAttemptId` | `576b2cdc-60a0-535d-a865-3e92171ff8c0` |
| `B.operationStatus` | `Failed` |
| `B.slotEvidence` | `slot2:State=Empty/DoorLocked=True/UnlockOutputReset=True` |
| `B.demandEnd` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled` |
| `B.journeyPosition` | `9/AwaitingGateArrival` |
| `C.finalStage` | `AwaitingGateArrival / -` |

## 照片指针

照片本身不进 git，这里只留指针。

模拟器 IO 下无人到场，没有照片；现场记录由驱动脚本按实际动作写出。

## checkpoint 序列

场景 C 的关键值是「期限到期后再等 20 分钟依然不结束」，而结算本身会覆盖掉那一刻的状态——
所以它只能由当时抓下的 checkpoint 回答，不能事后从库里读。

- `snapshots/01-00-ready/`
- `snapshots/02-c-deadline-reached/`
- `snapshots/03-c-plus-hold/`
- `snapshots/04-b-settled/`
- `snapshots/05-finalize/`

## 目录内容

- `assertions.json` —— 机器可读的判据结论，含现场记录原文与实测事实
- `timeline.jsonl` —— 一行一次 checkpoint，只追加
- `logs/` —— 每次远端拷贝的输出
- `snapshots/<序号>-<label>/` —— 每个 checkpoint 当时的库行、两端安装清单与车载端日志

**整个 `controlserver.db` 不在这里**。它是生产库，装着与本窗口无关的旅程，而 `evidence/` 进 git。
完整库落在 `C:\Users\szy\w2g-stage\field` 下的 `FW-SC1-<runId>`，需要时按 runId 找；
`run-demand-bearing-g3-vectors.ps1` 的 `-FieldRunRoot` 要的就是那个形状。

## 这份证据证明了什么，没证明什么

**证明了**：ADR-cross-0058 的决策 1、2、4、5 在 `AGV-L2-001`（L2 真装置彩排（本机））上、IO 接 slots-simulator
（`127.0.0.1:58412`）时，**软件闭环**的行为与判据一致；操作员的每一个动作由驱动脚本冒充，记录与 checkpoint 由它按实际动作写出。
车是不是真的在线路上走，这份证据不回答——现场窗口是，L2 彩排不是，看「现场」一栏。

**没有证明**：光幕极性、锁反馈时序与机械弹开行为。ADR-cross-0058 原话是模拟器证明不了这三样，而本窗口的 IO 正是模拟器——
这是 2026-09-11 改为全部无人值守时有意放弃的，见地图 Out of scope。

**没有证明**：完整闭环。受理→取货→录 SUBLOT→装货→安全检查→去关卡→卸货→收尾，以及自动充电与取消订单
两条支路，属于现场窗口二（[#20](https://github.com/trytoreachpeak0/8005-agv-program/issues/20)），不在这份证据里。

**没有证明**：另外两台车。本窗口只在 `AGV-L2-001` 上跑；三台上位机是同一个镜像的克隆，但 IO 接线是
逐车的，一台的绿说明不了另外两台。
