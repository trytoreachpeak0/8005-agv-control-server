# cs#541 整条 runner 的自检运行（调度放行的本机时段，2026-10-10）

都是自检覆盖（命令行传入提交，不动绑定），记为 `SELF_CHECK_OVERRIDE`，**不是正式 G3 证据**。正式证据由 cs#393 重移绑定后从头重跑。为控制体积，store 生成器的服务端日志 `store-generator/logs/control-server.out.log` 已删。

| 目录 | runner / 服务端 | 结论 | 本票判据 |
| --- | --- | --- | --- |
| `staged-selfcheck-d5f2c017` | `run-staged-g3.ps1`，cs `d5f2c017` + onboard `b9e67a53` + sim `fb5f7c59` + protocol `3f091cb2` | `STAGED_G3_RECOVERY_REPLAY_PASS` | FP-IS-06、FP-IS-07 的内容冲突判据全绿 |
| `demand-bearing-selfcheck-head-d5f2c017` | `run-demand-bearing-g3-vectors.ps1 -SelfCheckControlServerCommit d5f2c017` | `DEMAND_BEARING_SLICE_FAIL` | 三个冲突用例全绿 |
| `demand-bearing-selfcheck-m1-3b53407b` | 同上，服务端 + M1（冲突那条写进库） | `DEMAND_BEARING_SLICE_FAIL` | `sameMessageIdWithDifferentContentIsRefused` 红（线上应答正确，库内检查抓到），另两条绿，符合预期 |
| `demand-bearing-selfcheck-m3-7faebc85` | 同上，服务端 + M3（入站边界改回抛出断连） | `DEMAND_BEARING_SLICE_FAIL` | 三个冲突用例都红（`connectionEnded: true`），符合预期 |

三遍 demand-bearing 整轮都红在 `protocolAndBuildIdentityBoundToTheSharedBinding` 和 `restartedHostServesTheSameStore`，与本票判据无关：这两条把服务端报的 `protocolCommit` 与共享绑定比（`run-staged-g3.ps1` 参数默认值里的 `86575456`，protocol-v2.0.0），而这三遍跑的服务端是 protocol-v3.0.0（`3f091cb2`）。cs#393 把绑定移到 v3 后这两条就会对上。

失败与中断的运行都留着：

- `interrupted-disk-full-demand-bearing-m1-3b53407b`：C 盘写满（`No space left on device`）时中断，没有结论。随后在 C 盘有空间后重跑，即上表 M1。
- `build-failed-demand-bearing-m3-63fe7c61-cs8602`、`…-rerun`：M3 变异第一版写成 `if (rejected is not null) throw;`，可空分析报 CS8602，仓库把警告当错误，构建失败。不是磁盘问题，两次都是这个原因。改成 `if (messageType.Length > 0) throw;` 后即上表 M3。
