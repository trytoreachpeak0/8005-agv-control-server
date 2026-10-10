# control-server#335 开工查证：真车载端行驶中服务端的门锁事实

CI 真装置 run 36387029532（`l2.yml` 的 `real-rig` 作业，`-f rig=real`），场景 `real-onboard-in-transit-door-facts` 的**探针版**
（提交 `716b40df`，只记录、不判产品行为）。之后这条场景已改成带判据的正式版，本目录记录的是改之前那一轮。

## 四行核对

```
control-server @ 716b40dfb5447a0d75825c4ea46a6aa3d0295798
8005-agv-onboard-hmi @ 4c2d2dc14656f80e812f37128a7964c2c310217a
slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
real-onboard-in-transit-door-facts-01 -- PASS, 128s
```

停止条件 `RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE` 命中 1 行，是源码回显（带字面 `^[[36;1m`）。artifact
`real-rig-evidence` 371186 字节。

## 量到的数字（`snapshots/door-facts-*.json`）

| 项 | 结果 |
| --- | --- |
| 取货段空车行驶，每 500 ms 抽样 | 24 个样本，全部取到对得上当前代次与 `SafetyRevision` 的门锁摘要，`allTargetSlotsLocked` 全为 true |
| 关卡段有货行驶 | 29 个样本，同上 |
| 会话状态 | 行驶全程 `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY`，旅程 `ONBOARD_SESSION_NOT_READY` |
| 门锁摘要自身最旧 | 33.2 秒（变了才发） |
| 这一代入站消息最长间隔 | 1.98 秒（心跳） |
| 行驶中断线到新一代首条安全快照到库 | 2237 ms；会话行已换代而快照未到的空窗约 100 ms |
| 锁反馈强制为 0 到服务端收到 `allTargetSlotsLocked=false` | 116 ms；放回后 227 ms 收到恢复 |
| Modbus 不回应到收到 `SLOT_STATE_UNKNOWN` | 1058 ms；恢复后 1748 ms |

车载端在出发后约 1 秒内报 `VEHICLE_NOT_READY` 且 `unknownPresent=true`，之后改报 `ACTION_NOT_ALLOWED_IN_STATE`、
`unknownPresent=false`；两段里门锁一栏都照实报。所以「门锁未知」按 `SLOT_STATE_UNKNOWN` 认，不按 `unknownPresent`。

## 同目录旁边那一轮

`20260928-ci-36386347310-...` 是首轮，红：到站期限设成了 5 分钟，而它同时是装货后离站前的等待，旅程停在
`AwaitingStationDeparture` 180 秒超时，没进入关卡段。探针自身配置错，不是产品缺陷；取货段 24 个样本在那一轮也取到了。
