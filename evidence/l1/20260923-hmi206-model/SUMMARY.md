# hmi#206：cs#342 模型里的车改照新规则发握手安全快照

追踪票 trytoreachpeak0/8005-agv-onboard-hmi#206，配对 PR trytoreachpeak0/8005-agv-onboard-hmi#207（车载端修复）。
服务端集成分支基点 `fc3dda3c`（cs#342 合入）。本机 Release 构建，xunit v3 进程内运行器直接跑 `ControlServer.Tests.exe`。

| 文件 | 代码状态 | 结果 |
| --- | --- | --- |
| `01-red-skip-removed-old-vehicle.txt` | `ef9ba849`：去掉已知缺陷行与 `Skip`，模型里的车仍是旧行为 | `ASafetyChangeLostInFlightDoesNotGetTheNextHandshakeRefused` 两格都红，`LegitimateMessageRefused, "SafetyStateSnapshot (generation 2, handshake open)"` |
| `02-exploration-1000-new-vehicle.txt` | `9aad1a3c`：模型里的车照新规则 | `PrototypeMeasurement` 1000 个组合：没有任何一类违规（报告里没有 `first <类别>` 行）；录入请求 1000/1000 送到车上；`ack conflicts=0 vehicle regressions=0` |
| `03-reverse-old-vehicle-300.txt` | `9aad1a3c` 加变异 R1：`HandshakeSafetySnapshotVersion` 一律返回已接受的版本（即旧规则），`--no-incremental` 重编 `0 Error(s)` | 300 个组合里 `LegitimateMessageRefused: 129 of 300`，与 cs#342 原先报的 129/300 相同；最短形式 `synthetic [Safety(NotSafe,LostInFlight), BeginHandshake]`；回归用例两格红；CI 那一批（200 个固定种子）红：`96 violation(s) in 200 seeded sequences that no known defect accounts for`，全是 `... refused: ProtocolContentConflictException: safety revision N has conflicting content.` 跑完从备份还原，SHA-256 核对一致，再 `--no-incremental` 重编 |
| `04-reconnect-model-and-architecture-tests.txt` | 与 `9aad1a3c` 同一份工作树（提交前跑的） | `-class "ControlServer.Tests.ReconnectModel*" -class "*ArchitectureTests"`：92 条全过，3 条只在显式要求时跑（Not Run） |

## 读法

- 变异 R1 只改了取号那一行，「快照被确认后推进已接受版本」那一句还在；在旧规则下取的号本来就等于已接受版本，所以那一句这时什么都不做，这个状态就是修复之前的车。
- 129/300 在变异下原样回来，说明模型对这一类的判别力没有因为本 PR 的改动丢掉；修后 1000 个组合零违规，说明新规则下这一类确实归零，也没有冒出新类别。
- 模型测不到的格子见 control-server PR #344「模型的局限」：服务端进程重启；持货等单、途中追加、多停靠；取消与故障动作；一轮之内的「写上又覆盖」。本票改的是握手快照取号，不落在这些格子里。
