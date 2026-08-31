# 明文传输改造（票 02）服务端产品代码取证

日期：2026-08-31。基线：`ControlServer_MVP@e5ee065`。改动范围：`src/ControlServer.Host`、
`tools/ControlServer.FakeOnboard`、`tests/ControlServer.Tests`、`docs/ai-spec/slices.md`、
`README.md`、`ControlServer.http`。安装链（`scripts/`）与发布手册不在本票，留给票 03／04。

## 红侧（改产品代码之前）

`red-side.txt`：两条新测试在原产品代码上跑，两条都红，错误文本正是被拆的两处守卫。

- `OnboardTcpServerTests.NonLoopbackListenerStartsAndAcceptsPlaintextConnections` →
  `System.InvalidOperationException : A non-loopback Onboard listener requires a configured TLS server certificate.`
  （`OnboardTcpServer.ValidateConfiguration`，改前第 143 行）
- `OnboardVehicleSafetyEndpointsTests.AuthenticatedPlainHttpRequestReturnsTheFailClosedRiotProjection` →
  `Assert.IsType() Failure` Expected `Ok<OnboardVehicleSafetyResponse>` / Actual `StatusCodeHttpResult`
  （即 426 Upgrade Required）

## 绿侧

`green-side-tier1.txt`：`dotnet test tests/ControlServer.Tests -c Release`
→ **Failed: 0, Passed: 249, Skipped: 0, Total: 249**，41 s。跳过数为 0，不存在被静默跳过的覆盖。
Release 全解决方案构建 **0 warning 0 error**（`TreatWarningsAsErrors=true`），
`dotnet format --verify-no-changes` 退出码 0。

## 非 loopback 明文启动的运行期回读

`Invoke-PlaintextStartupProbe.ps1` → `plaintext-startup-probe.json` / `.log`。
隔离端口（58105／58107）与隔离 SQLite，未触碰已安装的生产服务（其监听仍为 loopback 58005／58007）。

| 判据 | 回读结果 |
| --- | --- |
| 非 loopback 明文监听能启动，全程零证书 | `Onboard NDJSON listener started on 0.0.0.0:58105; transport=plaintext`；listener 实为 `0.0.0.0:58105` 与 `0.0.0.0:58107` |
| `/health/live` 经 HTTP | `200`，body `{"status":"live"}` |
| 业务端口可建 TCP 连接 | `onboardTcpConnected: true` |
| 投影端点经 HTTP 可达且无凭据仍拒绝 | 匿名 `401` + `WWW-Authenticate: Bearer`；错误 Bearer 亦 `401` |

## 过时配置键的启动期拒绝（票 01 第 6 节）

`stale-key-startup-refusal.txt`：同一二进制、同一配置，仅额外注入
`OnboardTransport__serverCertificatePath`，进程拒绝启动：

```
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException:
OnboardTransport:serverCertificatePath was removed in this version; the Onboard transport is
plaintext. Delete the key from every appsettings file.
```

绿侧对照即上一节的探针——不带该键时同一检测器放行并正常启动，因此这条红不是恒红。

## 覆盖率差异声明（票 01 第 9 节要求）

`tests/ControlServer.Tests/OnboardTlsCertificateLoaderTests.cs` 已整个删除（被测类
`OnboardTlsCertificateLoader` 消失）。**本仓自此再无任何测试触及 Schannel**。它携带的
`IntegrationSlice=W2G-IS-00` trait 仍由另外 13 条测试携带，切片未失去覆盖。票 08 重建候选时须显式
说明这处与上一轮的差异，避免看起来像覆盖率无声下降。
