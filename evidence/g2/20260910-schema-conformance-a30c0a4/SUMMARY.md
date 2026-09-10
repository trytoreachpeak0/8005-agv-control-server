# CONTROL_SERVER_G2 八切片，`a30c0a4`：全部 FAIL，一个测试都没跑

- 日期：2026-09-10
- 实现：`8005-agv-control-server` `a30c0a4`（出站 schema 校验，8005-agv-program#33）
- 协议：`protocol-v0.3.0`，manifest 用 `vendor/8005-agv-protocol/protocol-v0.3.0/manifest/release.json`（与 tag 字节相同，`b6c81ca9…`）
- 结论：**八刀 `status: FAIL`、`testExitCode: 1`、`schemaConformance: null`——红的原因与被测代码无关，是门禁脚本的缺陷。保留作红证据。**

## 看到了什么

每一刀 15–35 秒就结束，没有 TRX，没有 schema 报告。日志里是构建错误，全部落在本次没有改动过的既有代码上：

```
tools\ControlServer.FakeRiot\RiotDataPlane.cs(186,36): error CA1859: Change type of parameter 'missions' from 'System.Collections.Generic.IReadOnlyList<ControlServer.FakeRiot.FakeMission>' to 'ControlServer.FakeRiot.FakeMission[]' for improved performance
src\ControlServer.Infrastructure\Persistence\WireToGateStore.cs(2204,45): error CA1859: Change type of parameter 'stops' ...
src\ControlServer.Infrastructure\Persistence\WireToGateStore.cs(2205,47): error CA1859: Change type of parameter 'demands' ...
```

## 根因

`dotnet` 按**当前工作目录**向上找 `global.json` 选 SDK，而不是按项目文件所在目录。这一轮是从工作区根 `C:\Users\szy\Desktop\8005-workspace` 启动的，那里没有 `global.json`，于是选中了本机最新的 `10.0.302`。它的分析器在 `AnalysisLevel=latest-recommended` 下把 `CA1859` 报成警告，`TreatWarningsAsErrors` 再把它变成构建错误。

对照：同一个 commit、同一条 `dotnet test ... --filter IntegrationSlice=W2G-IS-00`，在仓库目录里执行（选中 `global.json` 锁定的 `8.0.425`），零 `CA1859`，56 个测试通过，退出码 0。

`gate-result.json` 当时不记 SDK 版本，所以光看这八份结果分辨不出这一点。

## 修法

**同一个坑另一个会话已经修掉了**：PR #21（`fcfb1ad`，2026-09-10 08:25 合入 `ControlServer_MVP`）让 `scripts/test-wire-to-gate.ps1` 等四个构建入口先进仓目录再调 `dotnet`，并把 SDK 写进 `gate-result.json` 的 `dotnetSdkVersion`。本分支当时基于更早的 `683cc23`，所以这一轮撞上了。本分支一度自己又修了一遍（`ad3392c`），把 `ControlServer_MVP` merge 进来时让位于 `fcfb1ad` 的写法，只保留 schema 校验报告目录那一段。

修复之后的八切片证据在同级的新目录里，`gate-result.json` 自带 `dotnetSdkVersion`。
