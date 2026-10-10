# CI 真装置 run 36520273758：cs#380 审查修改之后的第三轮

结论：**2/2 PASS**。车载端用的是审查修改之后的最终 head `bf9270c`，里面有三处修复：ack 之后的重发、被替换的旧接收循环、失败循环的收尾进锁。

## 身份

| 项 | 值 |
| --- | --- |
| run | `36520273758`（`l2.yml`，`rig=real`，`mode=consecutive`，两个场景各 1 遍） |
| control-server | `f5ad958f3f8f520c8d650039853e2e18788d0380`（`fix/380-readiness-handoff-entry`） |
| 8005-agv-onboard-hmi | `bf9270c30dbb064eea69208e69f41696adbaa1fa`（`w2g/380-readiness-handoff-entry`） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |

三端提交是从日志里「Run real-onboard L2 scenarios」那一步读出来的，与 `commits.json` 一致。`RIG_*` 停止标记只以源码回显出现 1 次。

| 场景 | 结果 |
| --- | --- |
| `real-onboard-compensate-then-reconnect-01` | PASS 71s |
| `real-onboard-cancelled-rebuild-cargo-proof-01` | PASS 127s |

## 修后的形状

以下由 `readiness-shape.py` 和 `readiness-timeline.py` 各自独立算出：

- RecoveryRequired 发布之后、同一代下一次收到 READY 之前，没有出现 Ready 的发布，次数为 0。
- 「丢弃SessionReadiness」0 次。
- 1104 0 次。
- 服务端 1103 共 38 条，逐条按 messageId 在车载端都找到了「收到SessionReadiness」，缺失 0 条。

交接时刻：`faultCargoHandoff=True`（12:12:27.682）。

**失败收尾这条路径在真装置上走到了**：在 `real-onboard-compensate-then-reconnect-01` 里，中继断开连接之后，第 2 代的接收循环读到连接结束，依次发生了：

| 时刻 | 车载端日志 |
| --- | --- |
| 12:11:03.714 | `接收循环失败收尾：generation=2，EndOfStreamException` |
| 12:11:03.722 | `会话状态发布：source=receive-failed`，Disconnected |
| 12:11:05.726 | `会话状态发布：source=handshake-start`（重连开始） |
| 12:11:05.782 | `会话状态发布：source=session-accepted`，generation=3 |

收尾和它的 Disconnected 都在新握手的第一次发布之前完成，没有落到第 3 代会话上。「被替换的旧循环读到就绪行」那条路径在这一轮仍然没有走到，只由 G2 用例覆盖。

## 目录内容

目录结构与前两轮（`20260929-ci-36507086407-cs380-real-rig/`、`20260929-ci-36509989022-cs380-real-rig/`）相同。完整产物在这次 run 的 artifact `real-rig-evidence` 里。
