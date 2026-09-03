# ControlServer.ClockSkewProxy

挡在车载端和 ControlServer 的车辆安全投影之间，**只把响应里的 `observedAt` 挪一个可配置的偏移量**，别的一律原样转发。

```
车载端 ──GET──► 代理 ──GET──► ControlServer
                  │            /api/onboard/v1/vehicle-safety
                  └── observedAt += skewMs
```

## 为什么需要它

车载端拒绝一切盖在它自己「未来」的证据——`VehicleSafetySignal.IsFresh`，以及
`ControlServerVehicleSafetySignalProvider` 里同一条判定：

```csharp
if (_settings.MaximumEvidenceAgeMs <= 0 || observedAt > now) return false;
```

车载机时钟慢一点，服务端盖的 `observedAt` 就落在车载端的未来，判 `EVIDENCE_EXPIRED` → `UNKNOWN`
→ `vehicleStopped` 恒 false → 会话永远进不了 Ready。这就是
[`8005-agv-onboard-hmi#1`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/1)，
已在 `abb8e73` 修复为**有界容差**（`vehicleSafety.clockSkewToleranceMs`，默认 500 ms、上限 1000 ms）。

但 L2 里两端跑在同一台机器上共用一个时钟，而 `observedAt` 是 ControlServer 用自己的
`timeProvider` 盖的章（`HttpRiotMovementGateway.ReadVehicleSafetyAsync`），**偏差不会自己出现**，
缺陷和它的修复都没法验。这个代理就是那个偏差。

## 为什么这不是「自己写的假象」

- **被测的是车载端出厂的真代码**，那段新鲜度判定一行没改；
- **喂进去的输入和真的慢 N 毫秒的时钟喂进去的一模一样**——就那一次比较而言，「服务端时间戳比我的
  `now` 大 N 毫秒」是同一个事实。

**它不等价于什么，也要说清楚**：真的慢时钟会同时挪动车载端自己盖的每一个时间戳（journal、
`journeySnapshotMaxAgeMs`、协议消息的 `sentAt`）；代理只偏这一处比较。对 `#1` 恰好就是出问题的
那一处，但证据里不能说得比这更多。

**两条刻意不走的路**：改机器时钟（会动到整台机器上的一切，包括另外四个进程和操作员自己的会话），
以及把 `maximumEvidenceAgeMs` 设成 0 冒充（那走的是另一个分支，同一个症状、另一个原因，证明不了
`#1`）。

## 用法

```powershell
.\ControlServer.ClockSkewProxy.exe `
  --ClockSkewProxy:port=58413 `
  --ClockSkewProxy:target=http://127.0.0.1:58407 `
  --ClockSkewProxy:Seed:skewMs=100
```

`--ClockSkewProxy:target` **没有默认值**。别的替身回答的是一个此地并不存在的系统，这个不一样——
它转发给一台真服务器，猜默认值就是在猜哪一台。

| 项 | 值 |
| --- | --- |
| 数据面 | `GET /api/onboard/v1/vehicle-safety` |
| 控制面 | `/control/v1/health`、`/snapshot`、`/skew`、`/reset`、`/openapi.json` |
| 机器契约 | `openapi.json`，运行时在 `/control/v1/openapi.json` |
| 监听 | 仅 loopback，默认端口 58090（L2 用 58413） |

运行时改偏移：

```powershell
PUT /control/v1/skew  { "runId": ..., "commandId": ..., "skewMs": 2000 }
```

`skewMs` 为正 = 车载端时钟慢（证据落在它的未来），为负 = 车载端时钟快。范围 ±60000 ms。

## 三条边界

1. **`Authorization` 原样转发。**代理读不到车载端自己读不到的投影。
2. **上游的非 2xx 与非 JSON 响应原样透传。**把一个拒绝改写得更友好，就会盖掉车载端本该表现出来的
   fail-closed 行为。
3. **用 `JsonNode` 而不是强类型记录改写。**服务端以后新增的字段必须原样到达车载端，不能被这个替身
   悄悄丢掉。

## 快照里有什么

`forwardedRequests` 是**车载端确实走了这里**的证明。少了它，「会话没就绪」既可能是车载端在
fail-closed，也可能只是代理自己写坏了——两者在断言上长得一模一样。`lastUpstreamObservedAt` 与
`lastForwardedObservedAt` 记下最后一次进出的时间戳，证据里能直接看到偏移确实施加了。
