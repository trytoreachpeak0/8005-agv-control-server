# cs#573 修复前真装置：清单搅动下离站等待走不满（run 38067577157 之后的重派，run 38068425850）

CI `l2.yml` `rig=real`，run [38068425850](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38068425850)。服务端 `f6c2c8398bcec8bc650e1793a8f4cdd617a7dc5b`（分支 `fix/cs573-red-baseline`：反做实现 `6498c580`、投影仍是今天的严格读法，场景已改为装货提交之后才搅），车载端 `535c94fce47a10879ba1f404d04f603ba1a65bbf`，模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`。

结论 FAIL，红在预期的地方（`SUMMARY.md`、`assertions.json` 是运行器原样产出）：

| 判据 | 结论 | 实际 |
| --- | --- | --- |
| `L2-LC-01` 注入打中 | PASS | 离站等待期间搅乱 37 次 |
| `L2-LC-02` 80 秒内离站 | **FAIL** | 停在 `AwaitingStationDeparture`，没有 TO_GATE 单 |
| `L2-LC-03` 没报 `VEHICLE_NOT_READY` | **FAIL** | 9 次，约每 8～11 秒一次 |
| `L2-LC-04` 旅程走完 | 未判 | 车没离站，场景按设计跳过 |

这与现场 cs#566 站 15 是同一个形状：每闪一次，服务端清掉离站等待重计，20 秒一次都走不满。只入库判据、时间线与车载端日志，其余（服务端日志、库快照）留在 CI 的 artifact 里。
