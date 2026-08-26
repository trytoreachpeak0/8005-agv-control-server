# 8005 AGV ControlServer

WIRE_TO_GATE MVP 的服务端生产仓库。`ControlServer_MVP` 当前包含正式协议下的 SQLite 持久状态核、五步恢复握手、可靠 inbox/outbox、生产 Journey Worker、Demand/RIoT 意图、多仓命令与 outbox 原子建立、车载 `OperationResult` 内容哈希核验、装货事实提交、卸货四事实原子完成、断联收敛、只读 MesIngest V2 适配器、可运行 Host、Fake Onboard 和逐切片 G2 入口。

这仍不是整个双端 MVP Release Candidate：真实 RIoT 环境移动集成与车辆/Map/站点资格、真实 OnboardHmi、G3 和安装/现场验收仍是后续门禁。

正式协议绑定：

- tag: `protocol-v0.1.1`
- protocol commit: `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest SHA-256: `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- schema bundle SHA-256: `e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c`
- vectors SHA-256: `fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e`
- status: `APPROVED_RELEASE`

构建前必须使用 `global.json` 指定的 .NET SDK `8.0.424`。若 SDK 未加入 `PATH`，可先把 `WIRE_TO_GATE_DOTNET_EXE` 指向该版本的 `dotnet.exe`：

```powershell
.\scripts\build.ps1
```

运行全仓测试：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```

本地端口默认为：Onboard NDJSON `127.0.0.1:58005`，健康/版本 HTTP `127.0.0.1:58007`。非 loopback 监听必须配置 TLS PFX；车载凭据由 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 注入。SQLite 使用 EF Core 安装期迁移，不在运行时写 MesIngest。

启动 Host 后，可用 Fake Onboard 验证五步空恢复握手：

```powershell
$env:CONTROL_SERVER_ONBOARD_CREDENTIAL = '<external-secret>'
dotnet run --project .\src\ControlServer.Host -c Release
dotnet run --project .\tools\ControlServer.FakeOnboard -c Release -- --host 127.0.0.1 --port 58005
```

逐切片 G2 入口会先验证精确 protocol manifest 哈希，并要求新证据目录：

```powershell
.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice W2G-IS-00 `
  -ProtocolManifest <protocol-repo>\manifest\release.json -Output <new-evidence-directory>
```

`W2G-IS-01` 在 `protocol-v0.1.1` 中映射到专用轨迹 `CV-DEMAND-ACCEPT-TO-PICKUP`；旧 `v0.1.0` G2 证据不能继承。

实施入口见 [`docs/ai-spec/README.md`](docs/ai-spec/README.md)。秘密、PFX 和 CallApiKey 不得提交；生产值从受 ACL 保护的外部配置或环境变量注入。

## 生产 Journey Worker

`JourneyRuntime` 默认 `enabled=false`。启用前必须在外部生产配置中完整提供以下受控身份与规则，且
`MesIngest:sharedSecretEnvironmentVariable`、`RIoT:callApiKeyEnvironmentVariable` 和
`OnboardTransport:credentialEnvironmentVariable` 所指环境变量都必须已注入；任一缺失都会令 Host
启动验证失败，而不是退回默认车辆、地图、站点、容量或凭据：

- 指定 `agvId`、RIoT `vehicleKey`、`agvLifecycleGeneration`、`mapId` 与 RIoT 返回的 `mapIdentity`；
- 固定 pickup/gate 业务站点和 RIoT 数字站点、正数 `dispatchGeneration`；
- 显式 `WIRE_TO_GATE` 车辆任务白名单、DispatchZone 车辆白名单，以及 AREA/EQP 到 pickup 的唯一映射和具名路线证据；
- 正数 `admissionPolicyVersion`、具名 `admissionPolicyDeploymentId`，以及唯一的
  `stationTaskTypeAdmissions`。Worker 以版本化事务导入服务端 SQLite；同版本不同内容、版本倒退、未知站点／任务类型或重复关系均 fail closed；
- 已批准电量阈值、Onboard/RIoT 事实最大新鲜度、精确/前缀 PACKAGE 容量规则；
- `sublotBoxCountPath`：同一只读 MesIngest HTTP 身份下的 `SUBLOT_BOX_COUNT` 入口。响应必须精确返回
  `queryId=SUBLOT_BOX_COUNT`、原 Sublot、正整数 `maxBoxCount` 和 `observedAt`；失败、空值或身份不符均不开仓。

Worker 使用 `BackgroundService`、Options 启动验证、scoped DI 与 EF SQLite migration。它先持久化 backlog，
完成静态/动态硬准入及稳定排序，再通过已有 `JourneyIntakeCoordinator` 原子冻结 Demand、车辆租约、
`TO_PICKUP` 与 runtime。后续只从持久状态和已可靠接收的协议事实推进：可信 pickup 到站、投影/worklist、
Sublot、LOAD、发车安全、`TO_GATE`、可信 gate 到站、UNLOAD 与四事实原子完成。进程或连接重启后沿原
业务 ID 继续；未确认的服务端消息保留 MessageId/payload，并按当前 sessionGeneration 重新封装。
Sublot 提交时先按当前站点×任务类型策略预检；LOAD 的仓位操作、outbox 和允许决策快照在同一事务中
复检并冻结策略版本，后续策略更新不会重解释已承诺的物理操作。
