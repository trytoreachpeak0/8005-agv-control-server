# cs#541 内容冲突判据的红绿证据

由 `scripts/Test-StagedG3ContentConflict.ps1` 产生：一个不起桌面的自检，用 AST 从 `run-staged-g3.ps1` 里取出它自己的探针和库内判定 `Get-ContentConflictVerdict`，对指定服务端只跑带内容冲突判据的三个探针。**不是 G3 证据，不评任何切片。** 每个目录里的 `self-check-result.json` 是完整结果，`*-events.ndjson` 是探针与故障代理的逐条记录，`logs/control.out.log` 是服务端日志。最早五轮的构建日志 `logs/publish-control-server.log` 已删，`d5f2c017` 上的两轮（`green/5f3adc42-runner-d5f2c017`、M5）保留了，与历史 G3 证据的惯例一致。

| 目录 | 服务端 | 预期 | 结果 |
| --- | --- | --- | --- |
| `green/5f3adc42` | `5f3adc42`（出口绑定，cs#478 之后） | 四条全绿 | AS_EXPECTED |
| `red/pre-cs478-fba93a0e` | `fba93a0e`（PR #479 合入前的 `fp/v2-impl`；harness 回移到 protocol-v2.0.0） | 四条全红 | AS_EXPECTED |
| `red/mutation-m1-conflicting-retry-applied` | `5f3adc42` + `mutations/m1-*.patch` | 心跳、业务、强制恢复结果红，requestId 绿 | AS_EXPECTED |
| `red/mutation-m2-wrong-reason-code` | `5f3adc42` + `mutations/m2-*.patch` | 同上 | AS_EXPECTED |
| `red/mutation-m4-conflicting-request-taken` | `5f3adc42` + `mutations/m4-*.patch` | 只有 requestId 红 | AS_EXPECTED |
| `red/mutation-m5-conflicting-hello-ignored` | `5f3adc42` + `mutations/m5-*.patch`：冲突的 SessionHello 不回、不断 | 只有心跳那条红（它带着 SessionHello 钉子） | AS_EXPECTED：3003 ms 内流没有结束 |
| `green/5f3adc42-runner-d5f2c017` | `5f3adc42`，用 runner `d5f2c017` 重跑 | 四条全绿 | AS_EXPECTED：SessionHello 冲突 1 ms 内断开 |

`superseded/` 是 M1、M2 的第一次运行，当时的自检把「应红」写成「四条全红」，因此被标成 `NOT_AS_EXPECTED`。它们的判定与重跑一致（requestId 那条 M1/M2 碰不到，保持绿），留作记录。

`green/5f3adc42`、`red/pre-cs478-fba93a0e`、M1、M2、M4 用的是 `d5f2c017` 之前的 runner：SessionHello 钉子当时等 10 秒。服务端会把 6 秒静默的连接断掉（ADR-cross-0027，`SessionLiveness.Timeout`），所以那时「服务端什么都不做」也能被判绿。`d5f2c017` 改成 3 秒内必须断开并记录耗时，M5 证明它现在能分辨。其余判据这次没改，那几轮的结论照旧有效。

`mutations/m3-boundary-closes-connection.patch`（入站边界改回抛出断连）供 demand-bearing runner 的自检用，不在本表。
