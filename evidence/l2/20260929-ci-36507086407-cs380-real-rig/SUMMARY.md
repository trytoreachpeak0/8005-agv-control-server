# CI 真装置 run 36507086407：cs#380 的诊断日志在真装置上的时序

结论：**6/6 PASS**。这一轮是在加了诊断日志、修了车载端 ack 之后重发的覆盖以后跑的。日志显示，修前那个覆盖窗口在货物证明场景里**每一遍都会出现**。

## 身份

| 项 | 值 |
| --- | --- |
| run | `36507086407`（`l2.yml`，`rig=real`，`mode=consecutive-all`） |
| control-server | `ee3513a7de87ae408cc504c187f4dfae4440daed`（`fix/380-readiness-handoff-entry`） |
| 8005-agv-onboard-hmi | `3520505d6715192be613b938133994e8cd4da6b4`（`w2g/380-readiness-handoff-entry`，含 ack 之后重发的修复，**不含**旧接收循环那一条修复 `e49d85c`） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |

三端提交是从日志里「Run real-onboard L2 scenarios」那一步读出来的，与 `commits.json` 一致。

| 场景 | 遍 | 结果 |
| --- | --- | --- |
| `real-onboard-compensate-then-reconnect` | 01 / 02 / 03 | PASS 110s / 53s / 48s |
| `real-onboard-cancelled-rebuild-cargo-proof` | 01 / 02 / 03 | PASS 141s / 128s / 131s |

## 交接那一刻的先后（`real-onboard-cancelled-rebuild-cargo-proof`）

服务端判 `CARGO_HANDOFF_REQUIRED`，线上的原因码是 `SESSION_RECOVERY_REQUIRED`。这行就绪附在车载端那份 `SafetyStateSnapshot` 的应答后面发出。车载端日志如下，时间为 CST：

| 遍 | 服务端 1103 | 车载端「收到SessionReadiness」 | 随后的发布（按先后） | 判恢复入口 |
| --- | --- | --- | --- | --- |
| 01 | 09:24:40 RR | 09:24:40.461 | .461 `source=SafetyStateSnapshot-ack` Ready → .470 `source=SessionReadiness` RecoveryRequired | .471 `faultCargoHandoff=True` |
| 02 | 09:26:54 RR | 09:26:54.990 | .990 `SafetyStateSnapshot-ack` Ready → .999 `SessionReadiness` RecoveryRequired | 55.000 `True` |
| 03 | 09:29:04 RR | 09:29:04.266 | .266 `SafetyStateSnapshot-ack` Ready → .274 `SessionReadiness` RecoveryRequired | .274 `True` |

每一遍都是这个顺序。接收循环记下「收到」的同一毫秒，等待 ack 的那一方已经在发布；接收循环自己的发布晚 8–9 毫秒。两个线程每次都在几毫秒内先后写同一份会话状态。

修前的写法是等待方先读 `Current`、再原样写回。它读在接收循环写之前、写在之后，就会把 Ready 盖回去，这就是 run 36477303574 红的那一次。修后等待方在锁里读最新值，这里它先发的 Ready 就是当时的最新值，不是覆盖；接收循环随后写入 RecoveryRequired，三遍的入口都出现了。

`real-onboard-compensate-then-reconnect` 三遍，重连握手末尾的就绪（附在 `RecoveryStateReport` 应答后）都是先「收到」，再发布 `source=SessionReadiness`，没有 ack 之后的重发夹在中间。这个场景走的是补偿入口，所以 `faultCargoHandoff=False`，符合预期。

每一条服务端 1103 都能按 messageId 在车载端找到对应的「收到」。

## 目录内容

- `readiness-timeline.txt`：由同目录的 `readiness-timeline.py` 从 run 的完整 artifact 生成（`python readiness-timeline.py <artifact 解压目录>`），列出每一次 READY → RECOVERY_REQUIRED 翻转前后 2 秒内车载端的诊断行。
- `<场景>-<遍>/SUMMARY.md`、`assertions.json`、`timeline.jsonl`：编排器原样产出。
- `<场景>-<遍>/logs/onboard-app.log`：车载端应用日志，整份保留。
- `<场景>-<遍>/logs/control-server-readiness.log`：服务端 stdout 中 `SessionReadiness` 日志（EventId 1103/1104）的那些行。其余几 MB 是 EF 的 SQL 语句，没有入库；完整产物在 run 的 artifact `real-rig-evidence` 里。
- 快照目录（`snapshots/`）和其余组件日志都没有入库，原因同上。
