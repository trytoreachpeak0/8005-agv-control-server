# 8005 AGV ControlServer

WIRE_TO_GATE MVP 的服务端生产仓库。`ControlServer_MVP` 当前包含候选协议下的 SQLite 持久状态核、五步恢复握手、可靠 inbox/outbox、Demand/RIoT 意图、多仓批次、断联收敛、原子完成、只读 MesIngest V2 适配器、可运行 Host、Fake Onboard 和逐切片 G2 入口。

这仍不是整个双端 MVP Release Candidate：真实 RIoT 环境移动集成与车辆/Map/站点资格、真实 OnboardHmi、G3 和安装/现场验收仍是后续门禁。

正式协议绑定：

- tag: `protocol-v0.1.0`
- protocol commit: `3ad309ffd5f9a48a6cf390b51a81da2f47c814dd`
- manifest SHA-256: `92c19e74affe876902e1c64aa5cdbca845f5dbc93a8c82014a16627a26deb8d3`
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

实施入口见 [`docs/ai-spec/README.md`](docs/ai-spec/README.md)。秘密、PFX 和 CallApiKey 不得提交；生产值从受 ACL 保护的外部配置或环境变量注入。
