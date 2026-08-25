# 8005 AGV ControlServer

WIRE_TO_GATE MVP 的服务端生产仓库。当前 `ControlServer_MVP` 分支包含票 07 的可编译骨架、薄 Spec、Fake Onboard 入口和第一条预期失败的 SQLite 原子受理测试；它不是可用 MVP。

候选协议绑定：

- protocol commit: `72ddde595165468520d9f3a46b25e4aa4eec0c3f`
- manifest SHA-256: `e878d89e820535fe1eb64b85681b9c2994fb98646309e6ba768219c5c8735f2e`
- status: `CANDIDATE_UNAPPROVED`

构建前必须使用 `global.json` 指定的 .NET SDK `8.0.424`。若 SDK 未加入 `PATH`，可先把 `WIRE_TO_GATE_DOTNET_EXE` 指向该版本的 `dotnet.exe`：

```powershell
.\scripts\build.ps1
```

首条红测试：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release --filter "IntegrationSlice=W2G-IS-01"
```

实施入口见 [`docs/ai-spec/README.md`](docs/ai-spec/README.md)。秘密、PFX 和 CallApiKey 不得提交；生产值从受 ACL 保护的外部配置或环境变量注入。
