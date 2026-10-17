## 收尾（2026-10-14 20:48）：三车车载端已停，闸门关

**挪车后回读（20:45）**：用户在 RIoT 上手动把 agv01 开到 214、agv02 开到 215。之后 v2 库里 `ForeignRiotOrders`、`ManualChargingHolds`、`VehiclePurposeClaims`、`ChargingCycles`、`VehicleFaultStates`、`StationExclusivities` 都是 0 行，没有未清除的 `StructuralDispatchBlocks`，也没有新增意图。12:38Z 之后日志里没有外来单或保持相关的行，也没有 Warning 或 Error。所以明早开闸不会被这次挪车挡住。agv03 在 211 充电，93%。

**停栈（用户本人授权，三车都停，含 agv01）**：`10-start-onboard-stack.ps1 -SshAlias agv0N -Stop` 一次一车，三车都 exit 0。逐车读回：车载端进程已退出；日志簿 `%LOCALAPPDATA%\SQCD\8005AGV\onboard-journal.db` 还在；到 58105/58107 的连接为 0。服务端 58105 Established 也为 0。日志里有三条 `Onboard connection ended with a protocol or transport error`（10054），时间正好对上三次停栈，是客户端退出时的正常断开。

**现状**：V2 Running；`JourneyRuntime.enabled=True`、`RiotCreateDispatch.enabled=False`；区表只有表头，所以不受理新需求；两条已受理需求在待派里，各只剩 1 次站点超时。MVP 仍是 Stopped/Disabled。

**明早接着做**（接上一条交接评论）：
0. **起栈**：每车 `10-start-onboard-stack.ps1 -SshAlias agv0N -NoSimulator`。每次起栈前都问用户，agv01 另外单独问。看到握手 READY 之后再往下做。
1. 重跑 `20-check-production-io.ps1`，碰 agv01 先问用户。
2. 用户在两个做法里选一个：(a) 先导两条需求的小表，跑通后再放全量；(b) 有人有料时直接导全量合并表，那两条随全量一起重派。两个做法都要先离线比区名、再 dry-run。另外要用户确认：那两条在现场有没有人、有没有料，没有的话就接受它们作废。
3. 问用户开闸，带上新的 `io-check.json`。
