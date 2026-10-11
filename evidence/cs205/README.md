# control-server#205 证据：`real-onboard-inflight-load-reconnect` 转正式场景

两次 CI 真装置（`l2.yml` 的 `real-rig` 作业，vm01 `win11-01-control-server-desktop`），2026-10-11，各跑这一个场景一遍。
每个目录是 artifact `real-rig-evidence` 去掉 `_stage/`（运行时的临时构建副本）之后的全量，未精简（红轮的
`logs/control-server.out.log` 约 3.7 MB）；原始 artifact 见 run `38111328241`、`38111342946`。

| 目录 | run | 控制端 | 车载端 | 模拟器 | 结论 |
| --- | --- | --- | --- | --- | --- |
| `green-ci-38111328241/` | `38111328241` | `b5043c81`（本票场景提交） | `17668d22`（`w2g/fp-v2-impl` 顶端，含 onboard-hmi#127 的 `44b3aa6e`） | `fb5f7c59`（`main`） | PASS，七条全绿，105 秒 |
| `red-inject-ci-38111342946/` | `38111342946` | `0293565c`（注入分支，绝不合入） | 同上 | 同上 | FAIL，只红 `L2-IR-02`、`L2-IR-04`，134 秒 |

## 绿

- `L2-IR-01`：第一次断线重连后是第 2 代次，`RecoveryRequired`／`PENDING_FACT_RECONCILIATION_REQUIRED`，待对账含本次装货 attempt。
- `L2-IR-02`：放货关门后，这次装货的 `OperationResult` 在第 2 代次内送达并受理，会话 `Ready`，装货 `Committed`，没有靠第二次断线。
- `L2-IR-04`：第二次断线后是第 3 代次 `Ready`，旅程走到 `AwaitingStationDeparture`。
- `L2-IR-05`：同一 attempt 只有一条结果；`L2-IR-06`：全程没有 `ProtocolProblem`。

## 红（注入故障）

注入分支 `test/cs205-red-inject-result-refused-when-not-ready` 在 `OnboardMessageProcessor` 处理 `OperationResult` 的入口加了一段：会话不是 `Ready` 时只回 `DurableAck`、不应用结果。这是从服务端一侧复现 control-server#189 的两端互等（修复前是车载端未就绪不发结果，效果同样是「结果没被应用、会话一直等它」）。

- `L2-IR-02` 红：结果在第 2 代次送达了（收件箱有这一行），但装货停在 `Prepared`，会话仍是 `RecoveryRequired`／`PENDING_FACT_RECONCILIATION_REQUIRED`。
- `L2-IR-04` 红：第二次重连后会话回了 `Ready`，但装货仍是 `Prepared`，旅程停在 `AwaitingLoadResult`。
- 其余五条照常绿，说明红的只是被注入的那一处，不是场景或装置的毛病。

为什么不在 onboard-hmi#127 之前的车载端上取红：那个车载端早于协议 v3，与现在的服务端握不了手。旧红证据
`evidence/l2/20260919-cs189-inflight-reconnect-4a6790e-002`（一次性副本，读数有两处错）只作参考。
