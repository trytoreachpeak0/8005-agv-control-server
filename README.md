# 8005 AGV ControlServer

WIRE_TO_GATE MVP 的服务端生产仓库。`ControlServer_MVP` 当前包含正式协议下的 SQLite 持久状态核、五步恢复握手、可靠 inbox/outbox、生产 Journey Worker、RIoT Map/Station 目录解析、Demand/RIoT 意图、多仓命令与 outbox 原子建立、车载 `OperationResult` 内容哈希核验、装货事实提交、卸货四事实原子完成、断联收敛、只读 MesIngest V2.2 适配器、可运行 Host、Fake Onboard 和逐切片 G2 入口。

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

本地端口默认为：MesIngest V2.2 `127.0.0.1:5088`、Onboard NDJSON `127.0.0.1:58005`，健康/版本 HTTP `127.0.0.1:58007`。MesIngest 仅 loopback 绑定时不要求 SharedSecret；远程绑定仍必须使用外部 Bearer secret。Onboard NDJSON 与车辆安全投影均为明文传输，不再需要任何证书；非 loopback 监听由部署方在厂内网自行隔离。车载凭据由 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 注入，明文过网是本版本已知限制。SQLite 使用 EF Core 安装期迁移，不在运行时写 MesIngest。

启动 Host 后，可用 Fake Onboard 验证五步空恢复握手：

```powershell
$env:CONTROL_SERVER_ONBOARD_CREDENTIAL = '<external-secret>'
dotnet run --project .\src\ControlServer.Host -c Release
dotnet run --project .\tools\ControlServer.FakeOnboard -c Release -- --host 127.0.0.1 --port 58005
```

逐切片 G2 入口会先验证精确 protocol manifest 哈希，并要求新证据目录：

```powershell
.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice FP-IS-00 `
  -ProtocolManifest <protocol-repo>\manifest\release.json -Output <new-evidence-directory>
```

切片家族是 `FP-IS-00`～`FP-IS-15`，**替换**（不并存）`W2G-IS-00`～`07`；`FP-IS-00`～`07` 与旧的八条一一对应，
关系是「v2 下的重证」而非可沿用的通过结论。`FP-IS-01` 映射到专用轨迹 `CV-DEMAND-ACCEPT-TO-PICKUP`。
**证据不能跨协议换代继承**：`W2G-IS-01` 当年在 `protocol-v0.1.1` 中的重映射就作废过一次 `v0.1.0` 的 G2 证据，
这次切到 v2 是第二次。已有证据目录保留写入时的 `W2G-IS-NN` 编号，不改名。

实施入口见 [`docs/ai-spec/README.md`](docs/ai-spec/README.md)。秘密、PFX 和 CallApiKey 不得提交；生产值从受 ACL 保护的外部配置或环境变量注入。

## 生产 Journey Worker

`JourneyRuntime` 默认 `enabled=false`。启用前必须在外部生产配置中完整提供以下受控身份与规则。RIoT
和 Onboard 所指环境变量必须已注入；MesIngest 只有非 loopback 地址才要求 SharedSecret。任一必需值缺失
都会令 Host 启动验证失败，而不是退回默认车辆、地图、站点、容量或凭据：

- 指定 `agvId`、RIoT `vehicleKey`、`agvLifecycleGeneration`、`mapId` 与 RIoT 返回的 `mapIdentity`；
- 固定 `关卡` 的业务站点名与 RIoT 数字站点，及正数 `dispatchGeneration`；pickup 不再配置固定值；
- 通过获准的 `GET /api/imap/v1/mapInfo/stations/{mapId}` 读取整张 Map 的 Station 清单。只有由一至三个合法 AREA 编码以下划线连接的名称才是机台站；Demand 的 AREA 必须唯一匹配，零个或多个匹配均阻断；普通公共站点不因此形成全图失败；
- 显式 `WIRE_TO_GATE` 车辆任务白名单和单车 DispatchZone。每条 Demand 仍只冻结一个由 `AREA + EQP` 解析的机台站，并严格执行 `TO_PICKUP`、`TO_GATE` 两个单段订单；不会把多个匹配 Demand 合并成多站订单；
- 正数 `admissionPolicyVersion` 和具名 `admissionPolicyDeploymentId`。Worker 从当前原子目录中的 AREA 机台站形成本次 WIRE_TO_GATE 站点准入集并以版本化事务导入 SQLite；同版本不同内容或版本倒退均 fail closed，地图变更不能静默换站；
- 已批准电量阈值、Onboard/RIoT 事实最大新鲜度、精确/前缀 PACKAGE 容量规则；
- `sublotBoxCountPath`：同一只读 MesIngest HTTP 身份下的 `SUBLOT_BOX_COUNT` 入口。响应必须精确返回
  `queryId=SUBLOT_BOX_COUNT`、原 Sublot、正整数 `maxBoxCount` 和 `observedAt`；失败、空值或身份不符均不开仓。

当前受控现场身份已登记并只读回验为 `老厂前线新多仓位1`、RIoT `vehicleKey=BROKERX-0c20ff0600d644869a6a80c186065d85`、首次生命周期代次 `1`、`mapId=25`、`mapIdentity=老厂前线new`、固定关卡 `关卡/210`。RIoT CallApiKey 已按部署负责人要求持久化到 Windows User 范围的 `CONTROL_SERVER_RIOT_CALL_API_KEY`；从新打开的终端或刷新后的登录会话启动 Host，即可继承该值，密钥正文不进入仓库。MesIngest 合同固定为 `2026.08.new-mes-ingest.v2.4`／schema 29，并要求精确的 capability id+version 集合；`sublotBoxCountPath` 固定为 `/api/v2/sublot-box-count`。Onboard 凭据、容量和电量参数仍必须在启用前由外部配置及现场事实补齐。

Worker 使用 `BackgroundService`、Options 启动验证、scoped DI 与 EF SQLite migration。它先持久化 backlog，
完成静态/动态硬准入及稳定排序，再通过已有 `JourneyIntakeCoordinator` 原子冻结 Demand、车辆租约、
`TO_PICKUP` 与 runtime。后续只从持久状态和已可靠接收的协议事实推进：可信 pickup 到站、投影/worklist、
Sublot、LOAD、发车安全、`TO_GATE`、可信 gate 到站、UNLOAD 与四事实原子完成。进程或连接重启后沿原
业务 ID 继续；未确认的服务端消息保留 MessageId/payload，并按当前 sessionGeneration 重新封装。
Sublot 提交时先按当前站点×任务类型策略预检；LOAD 的仓位操作、outbox 和允许决策快照在同一事务中
复检并冻结策略版本，后续策略更新不会重解释已承诺的物理操作。

## 本机 Windows Service 部署

使用新目录生成绑定源提交和逐文件 SHA-256 的 `win-x64` 自包含包：

```powershell
.\scripts\Publish-ControlServer.ps1 -OutputPath <new-package-directory>
```

首次本机安装必须从提升权限的 PowerShell 运行，并显式确认 User→Machine RIoT 秘密复制。安装器拒绝
覆盖已有同名服务或安装目录，收紧安装与数据目录 ACL，以 `LocalSystem` 自动服务安装，并完成 HTTP
live、停止／启动、重启和版本回读。**两条链路都是明文，安装过程不生成、不导入任何证书**；监听地址由
`-ListenAddress`／`-HealthBindAddress` 决定，默认仍是 `127.0.0.1`。它保持
`JourneyRuntime.enabled=false`，不会调用 RIoT mutation、创建订单或移动车辆：

```powershell
.\scripts\Install-ControlServerLocal.ps1 `
  -PackagePath <package-directory> `
  -ResultPath <new-result-json> `
  -CopyUserRiotSecretToMachine
```

RIoT CallApiKey 和 Onboard credential 只存在于受 ACL 保护的外部位置或 Windows 环境变量，不进入
Git、包清单或安装结果。异机部署与凭据明文过网的已知限制见
[`docs/RELEASE-CANDIDATE.md`](docs/RELEASE-CANDIDATE.md) 第 4.4 与 11.1 节。

升级既有本机服务时使用新目录包和新结果路径。升级器在停服后以管理员 ACL 备份安装目录与完整数据根，
保留现有 `appsettings.Production.json` 与秘密并把它迁移成明文键集，清除 `certs\` 与机器级证书口令
变量，失败时恢复原二进制与 SQLite。`CurrentUser\Root` 里的旧自签根需按手册 4.5 人工删除：

```powershell
.\scripts\Update-ControlServerLocal.ps1 `
  -PackagePath <new-package-directory> `
  -ResultPath <new-result-json> `
  -VerifySafetyProjectionReadOnly
```

`-VerifySafetyProjectionReadOnly` 只调用已认证的 `GET /api/onboard/v1/vehicle-safety`，不会启用 Journey
Runtime、调用 RIoT mutation、创建订单或移动车辆。
