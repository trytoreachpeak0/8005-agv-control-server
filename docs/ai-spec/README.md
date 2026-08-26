# ControlServer WIRE_TO_GATE 薄实施 Spec

本目录是代码导航，不复制需求正文。权威顺序为：需求基线与 accepted ADR → 共享协议候选的机器资产 → WIRE_TO_GATE MVP 交接与切片索引 → 本目录。冲突时停止并提协议/需求变更，不在本仓平行改写 wire contract。

## 固定身份与边界

- 分支：`ControlServer_MVP`
- 协议 release：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- schema bundle：`e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c`
- vectors：`fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e`
- 状态：`APPROVED_RELEASE`；所有 G2/G3 证据必须绑定该精确身份。
- `W2G-IS-01` 使用专用轨迹 `CV-DEMAND-ACCEPT-TO-PICKUP`；通用重试与首结果重放轨迹继续归属 `W2G-IS-06`。
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
