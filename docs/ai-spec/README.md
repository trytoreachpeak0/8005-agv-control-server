# ControlServer WIRE_TO_GATE 薄实施 Spec

本目录是代码导航，不复制需求正文。权威顺序为：需求基线与 accepted ADR → 共享协议候选的机器资产 → WIRE_TO_GATE MVP 交接与切片索引 → 本目录。冲突时停止并提协议/需求变更，不在本仓平行改写 wire contract。

## 固定身份与边界

- 分支：`ControlServer_MVP`
- 协议 release：`protocol-v0.1.0@3ad309ffd5f9a48a6cf390b51a81da2f47c814dd`
- manifest：`92c19e74affe876902e1c64aa5cdbca845f5dbc93a8c82014a16627a26deb8d3`
- 状态：`APPROVED_RELEASE`；所有 G2/G3 证据必须绑定该精确身份。
- MesIngest 只读；RIoT 只由 ControlServer 通过具名端口调用；Onboard 只接收业务级协议，不接收原始 IO。

## 事务与副作用顺序

先在 SQLite 提交稳定身份、inbox/outbox 或业务意图，再 ACK 或执行外部副作用。HTTP/TCP/RIoT 不进入数据库事务。外部结果未知保持原业务键、`upperId`、MessageId 和绑定并先对账。正常卸货成功必须把 UnloadBatch、StopClosureCommit、Demand 成功和 TransportDemandCompletion 放在一个事务。

## 代码落点

- `Domain`：身份、状态与不变量；无网络/EF/UI。
- `Application`：用例与 `IMesIngestCatalog`、`IRiotMovementGateway`、`IOnboardPeer` 等端口。
- `Infrastructure`：EF SQLite、TLS/NDJSON、MesIngest/RIoT 适配器。
- `Host`：Windows Service 组合根、配置、健康与版本端点。
- `FakeOnboard`：只模拟协议可观察事实，不证明车载 UI/IO。
- `Conformance`：隔离的 G2 runner，不进入 Host。

每个切片先写红测试，只改当前切片最小路径。禁止把 Fake 变成第二个任务引擎、在 Host 引入 Corvus/STJ 10、对 RIoT 写请求自动重试、使用 EF InMemory 代替 SQLite、用文件日志代替业务审计或把 `ready` 在未完成五步恢复时置为成功。

逐切片约束见 [`slices.md`](slices.md)。
