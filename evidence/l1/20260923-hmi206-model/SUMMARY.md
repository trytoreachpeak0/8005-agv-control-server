# hmi#206：cs#342 模型里的车改照新规则发握手安全快照

追踪票 trytoreachpeak0/8005-agv-onboard-hmi#206，配对 PR trytoreachpeak0/8005-agv-onboard-hmi#207（车载端修复）。
服务端集成分支基点 `fc3dda3c`（cs#342 合入）。本机 Release 构建，xunit v3 进程内运行器直接跑 `ControlServer.Tests.exe`。

| 文件 | 代码状态 | 结果 |
| --- | --- | --- |
| `01-red-skip-removed-old-vehicle.txt` | `ef9ba849`：去掉已知缺陷行与 `Skip`，模型里的车仍是旧行为 | `ASafetyChangeLostInFlightDoesNotGetTheNextHandshakeRefused` 两格都红，`LegitimateMessageRefused, "SafetyStateSnapshot (generation 2, handshake open)"` |
| `02-exploration-1000-new-vehicle.txt` | `9aad1a3c`：模型里的车照新规则 | `PrototypeMeasurement` 1000 个组合：没有任何一类违规（报告里没有 `first <类别>` 行）；录入请求 1000/1000 送到车上；`ack conflicts=0 vehicle regressions=0` |
| `03-reverse-old-vehicle-300.txt` | `9aad1a3c` 加变异 R1：`HandshakeSafetySnapshotVersion` 一律返回已接受的版本（即旧规则），`--no-incremental` 重编 `0 Error(s)` | 300 个组合里 `LegitimateMessageRefused: 129 of 300`，与 cs#342 原先报的 129/300 相同；最短形式 `synthetic [Safety(NotSafe,LostInFlight), BeginHandshake]`；回归用例两格红；CI 那一批（200 个固定种子）红：`96 violation(s) in 200 seeded sequences that no known defect accounts for`，全是 `... refused: ProtocolContentConflictException: safety revision N has conflicting content.` 跑完从备份还原，SHA-256 核对一致，再 `--no-incremental` 重编 |
| `04-reconnect-model-and-architecture-tests.txt` | 与 `9aad1a3c` 同一份工作树（提交前跑的） | `-class "ControlServer.Tests.ReconnectModel*" -class "*ArchitectureTests"`：92 条全过，3 条只在显式要求时跑（Not Run） |

## 第二轮：模型里的车在会话不可发时的行为（`39362cf6`）

调度指出模拟车与真车的另一处差异（早就存在，与 #205 无关）：模型里的车在连接不在或握手进行中发生安全变化，写进持久日志、等下一次握手补发；真车载端这时不发、不记日志，会话一变为 `Ready`/`RecoveryRequired` 就按此刻的状态补发一条，号取 `max(_nextSafetyStateVersion, 已接受 + 1)`，而且每换一代都发一次（读 `WireToGateBusinessService.cs:887-905`、`:1891-1957`、`:2069-2073`，`w2g/fp-v2-impl@1184bb07`）。`39362cf6` 照改；`06-replay-republish-trace.txt` 是两条序列的逐步记录，能看到「补发 v8 → 快照 v9 → 就绪后补发 v10」与「握手中的变化不发 → 快照 v8 → 就绪后补发 v9」。

| 文件 | 代码状态（`39362cf6` 上的变异，脚本 `cs347-explore.sh`，每种 `--no-incremental` 重编 `0 Error(s)`、跑完还原并核 SHA-256） | 组合数 | 结果 |
| --- | --- | --- | --- |
| `05-E1-new-vehicle.txt` | 不改 | 1000 | 没有任何一类违规；录入请求 1000/1000 |
| `05-R2a-journal-only-keep-republish.txt` | 会话不可发时改回「只进日志、下次握手补发」，就绪后补发保留 | 300 | 没有任何一类违规 |
| `05-R2b-journal-only-no-republish.txt` | 两半都改回（即 `9aad1a3c` 的模型） | 300 | 没有任何一类违规（与 `02` 的 1000 组合一致） |
| `05-R3-republish-reuses-accepted.txt` | 就绪后补发不取下一版、沿用已接受版本（与握手快照同号） | 300 | `LegitimateMessageRefused: 259 of 300`，形如 `SafetyStateChanged (generation 2, handshake done) refused: ... safety revision 11 has conflicting content.`；连带 `StuckAtPickup 184`、`EntryNeverReachedVehicle 67`、`EveryRoundThrows 8` |
| `05-R1-old-snapshot-rule-new-model.txt` | 新模型上把握手快照改回旧规则（一律用已接受版本） | 300 | `LegitimateMessageRefused: 57 of 300`，`SafetyStateSnapshot (generation 3, handshake open) ... has conflicting content.` |
| `07-reconnect-model-and-architecture-tests-39362cf6.txt` | 与 `39362cf6` 同一份工作树（提交前跑的） | — | 三个 `ReconnectModel*` 类加 `*ArchitectureTests` 92 条全过 |

## 读法

- 变异 R1 只改了取号那一行，「快照被确认后推进已接受版本」那一句还在；在旧规则下取的号本来就等于已接受版本，所以那一句这时什么都不做，这个状态就是修复之前的车。
- 129/300 在变异下原样回来，说明模型对这一类的判别力没有因为本 PR 的改动丢掉；修后 1000 个组合零违规，说明新规则下这一类确实归零，也没有冒出新类别。
- R2a、R2b 与 E1 结果相同，按构造就该相同（推的）：新规则下，握手中发生的变化不管走哪条路报出去，号都比同代已经记下的大——进了日志就在下一次握手里补发，补发会让握手快照取下一版；不进日志就在就绪后补发，号取已接受的下一版，比刚确认的快照大。所以「只进日志还是就绪后补发」决定的是这条变化由哪一步送到，不决定会不会同号。这一格的判别力由 R3 给：就绪后补发一旦不取下一版，259/300 被判冲突，说明模型确实走到了「就绪后补发对上刚确认的快照」这一格。
- R1 从 129/300 降到 57/300（推的）：新模型里握手中与断线时的变化不再进日志，「补发的变化首次送达、同代快照同号」只剩「连接在这一条送到之前断了」（`LostInFlight`）一条来路，所以命中少了；这一类仍然找得到。
- 模型测不到的格子见 control-server PR #344「模型的局限」：服务端进程重启；持货等单、途中追加、多停靠；取消与故障动作；一轮之内的「写上又覆盖」。本票改的是握手快照取号，不落在这些格子里。
