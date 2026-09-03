# L2 场景证据：real-onboard-clock-skew

结论：**FAIL**

失败原因：Timed out after 120s waiting for: the demand was refused because the onboard cannot vouch for departure safety. Last observed: "ONBOARD_FACTS_NOT_READY"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260903T101421069Z` |
| agvId | `AGV-L2-001` |
| clockSkewMs | `100` |
| controlServerCommit | `641ca9dcb46a1174ed6873329b8a8a9b7f1e3cd6` |
| onboardHmiCommit | `60a0efdbf14b8195a1c1fd5afa8e6f39945279aa` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260903T101421069Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的安全投影确实经过了偏差代理，且 observedAt 被推后 | PASS | `forwardedRequests >= 1，位移 = 100 ms` | `forwardedRequests = 2，位移 = 100 ms` |
| 车载端时钟慢 100 ms（容差内）时会话正常建立 | PASS | `Ready` | `Ready` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
