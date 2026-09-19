# `L2-DA-09` 红绿证据（onboard-hmi#124）

`real-onboard-durable-ack-lost` 新增判据 `L2-DA-09`：丢掉一份已完成装货的 `DurableAck`、车重连补发被确认之后，
从会话重回 `Ready` 到卸货等操作员为止，HMI 上从未出现「上次装货操作未完成」。

服务端 `36066949`（本分支）、模拟器 `fb5f7c59`，真装置各跑一遍（2026-09-19 18:36–18:39）：

| 目录 | 车载端 | 结论 |
| --- | --- | --- |
| `green/onboard-cb6f61d-001/` | `cb6f61dd`（onboard-hmi PR #126，含修复） | PASS 10/10；`L2-DA-09` 90 次扫描、0 次出现 |
| `red/onboard-4a6790e-001/` | `4a6790e5`（修复前的 `w2g/fp-v2-impl`） | FAIL，只红 `L2-DA-09`：84 次扫描，看到「上次装货操作未完成：1号仓，需要管理员恢复。」；其余 9 条 PASS |

journal 里同一窗口读的服务端 `OnboardAlarmSnapshots` 诊断：绿 `False`，红 `True`（缺陷版本把
`ONBOARD_SLOT_OPERATION_UNFINISHED` 报上了服务端），只作诊断不作判据。
