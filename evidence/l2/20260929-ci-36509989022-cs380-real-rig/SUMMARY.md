# CI 真装置 run 36509989022：cs#380 最终代码上的第二轮

结论：**6/6 PASS**。这一轮的车载端是 cs#380 的最终代码，两处修复都在里面：一是 ack 之后的重发不再覆盖刚收到的就绪；二是旧接收循环的就绪行不再发布。日志显示的是修后应有的样子。

## 身份

| 项 | 值 |
| --- | --- |
| run | `36509989022`（`l2.yml`，`rig=real`，`mode=consecutive-all`） |
| control-server | `f5c40ad026c1cd137175fa4045fe8c54dd814171`（`fix/380-readiness-handoff-entry`） |
| 8005-agv-onboard-hmi | `3dd939284762066ff925b85cc6149bcdbafedad9`（`w2g/380-readiness-handoff-entry`） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |

三端提交是从日志里「Run real-onboard L2 scenarios」那一步读出来的，与 `commits.json` 一致。`RIG_*` 停止标记只以源码回显出现 1 次。

| 场景 | 遍 | 结果 |
| --- | --- | --- |
| `real-onboard-compensate-then-reconnect` | 01 / 02 / 03 | PASS 76s / 44s / 45s |
| `real-onboard-cancelled-rebuild-cargo-proof` | 01 / 02 / 03 | PASS 125s / 131s / 127s |

## 修后的形状（`readiness-shape.txt`，由 `readiness-shape.py` 生成）

- **「需要恢复」之后没有再发布「就绪」**：六遍里，凡是发布了 RecoveryRequired 之后、同一代下一次收到 READY 之前，都没有出现 Ready 的发布，次数为 0。脚本先用一份故意放了一次违例和一次丢弃的假日志验证过，两种都能报出来。第一轮 run 36507086407 用同一脚本复查，也是 0。
- **「丢弃SessionReadiness」出现 0 次**：这两个场景的重连，是由中继断开或者车载端断电重启引起的。断开的那一刻，旧连接上没有正在处理的就绪行，所以不需要丢弃任何东西。「旧循环的就绪行被丢弃」这条路径，目前只由车载端 G2 用例 `AReplacedReceiveLoopDoesNotPublishItsOldSessionsReadinessOverTheNewOne` 覆盖，真装置上没有被走到。
- **1103 对得上**：服务端 1103 共 107 条，逐条按 messageId 在车载端都找到了「收到SessionReadiness」，缺失 0 条。1104（握手中压住）出现 0 次。

## 交接那一刻（`real-onboard-cancelled-rebuild-cargo-proof`）

| 遍 | 服务端 1103 | 车载端「收到」 | 随后的发布 | 判恢复入口 |
| --- | --- | --- | --- | --- |
| 01 | 09:58:47 RR（附在 `SafetyStateSnapshot eff020b9` 的应答后） | 09:58:47.796 | .796 `SafetyStateSnapshot-ack` Ready → .804 `SessionReadiness` RecoveryRequired | .805 `faultCargoHandoff=True` |
| 02 | 10:00:55 RR（`fa9e3978`） | 10:00:55.058 | .058 Ready → .067 RecoveryRequired | .068 `True` |
| 03 | 10:03:04 RR（`6e29220d`） | 10:03:04.207 | .207 Ready → .215 RecoveryRequired | .216 `True` |

先后顺序和第一轮一样：等待 ack 的一方先发布，接收循环晚 8–9 毫秒。等待方发布的 Ready，是它在锁里读到的当时最新值，接收循环随后写入 RecoveryRequired，所以没有被覆盖回去。三遍的入口都出现了。

## 目录内容

与 `20260929-ci-36507086407-cs380-real-rig/` 相同：每一遍的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`、完整的车载端应用日志，以及服务端 stdout 中 1103/1104 那些行。另外有 `readiness-timeline.*`（每次就绪翻转前后的先后）和 `readiness-shape.*`（上面那三项检查）。快照目录和其余组件日志没有入库，完整产物在 run 的 artifact `real-rig-evidence` 里。
