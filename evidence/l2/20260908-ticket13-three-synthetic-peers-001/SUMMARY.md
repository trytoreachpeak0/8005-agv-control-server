# L2 场景证据：three-synthetic-peers

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260907T165022119Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `646624acc98eb3d7cacb330eadb1f3f3c8ecc76f` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260907T165022119Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 编排器按 setup 起了三个合成车载端 | PASS | `3` | `3` |
| 三个控制面各占一个端口 | PASS | `3` | `3` |
| 三个车载端各报各的 AgvId | PASS | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` |
| AGV-FAKE-001 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-001` | `AGV-FAKE-001` |
| AGV-FAKE-002 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-002` | `AGV-FAKE-002` |
| AGV-FAKE-003 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-003` | `AGV-FAKE-003` |
| 三个控制面报出三个互不相同的身份 | PASS | `3` | `3` |
| 服务端当前只持有第一台车的会话（accept 循环串行，票 09 前如此） | PASS | `AGV-FAKE-001` | `AGV-FAKE-001` |
| AGV-FAKE-001 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
