# cs#541 内容冲突判据的红绿证据

由 `scripts/Test-StagedG3ContentConflict.ps1` 产生：一个不起桌面的自检，用 AST 从 `run-staged-g3.ps1` 里取出它自己的探针和库内判定 `Get-ContentConflictVerdict`，对指定服务端只跑带内容冲突判据的三个探针。**不是 G3 证据，不评任何切片。** 每个目录里的 `self-check-result.json` 是完整结果，`*-events.ndjson` 是探针与故障代理的逐条记录，`logs/control.out.log` 是服务端日志。构建日志已删。

| 目录 | 服务端 | 预期 | 结果 |
| --- | --- | --- | --- |
| `green/5f3adc42` | `5f3adc42`（出口绑定，cs#478 之后） | 四条全绿 | AS_EXPECTED |
| `red/pre-cs478-fba93a0e` | `fba93a0e`（PR #479 合入前的 `fp/v2-impl`；harness 回移到 protocol-v2.0.0） | 四条全红 | AS_EXPECTED |
| `red/mutation-m1-conflicting-retry-applied` | `5f3adc42` + `mutations/m1-*.patch` | 心跳、业务、强制恢复结果红，requestId 绿 | AS_EXPECTED |
| `red/mutation-m2-wrong-reason-code` | `5f3adc42` + `mutations/m2-*.patch` | 同上 | AS_EXPECTED |
| `red/mutation-m4-conflicting-request-taken` | `5f3adc42` + `mutations/m4-*.patch` | 只有 requestId 红 | AS_EXPECTED |

`superseded/` 是 M1、M2 的第一次运行，当时的自检把「应红」写成「四条全红」，因此被标成 `NOT_AS_EXPECTED`。它们的判定与重跑一致（requestId 那条 M1/M2 碰不到，保持绿），留作记录。

`mutations/m3-boundary-closes-connection.patch`（入站边界改回抛出断连）供 demand-bearing runner 的自检用，不在本表。
