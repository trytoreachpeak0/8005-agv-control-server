# WIRE_TO_GATE MVP Release Candidate 操作手册

本文件是发布候选的唯一操作入口。按本文件从零构建、校验、安装、启动、停止、重启、定位日志与证据、
回滚，不需要任何原开发会话的上下文。

发布候选由三个版本绑定的仓库共同构成：

| 组件 | 仓库 | 分支 | agent 写权限 |
| --- | --- | --- | --- |
| ControlServer | `https://github.com/trytoreachpeak0/8005-agv-control-server.git` | `ControlServer_MVP` | 可写 |
| OnboardHmi | `https://github.com/trytoreachpeak0/8005-agv-onboard-hmi.git` | `OnboardHmi_MVP` | **只读** |
| 协议 | `https://github.com/trytoreachpeak0/8005-agv-protocol.git` | tag `protocol-v0.1.1` | 审批门禁 |

精确的 commit、哈希与协议身份不在本文件中重述，一律以发布候选根目录的 `release-manifest.json`
为准；该文件由构建脚本从产物派生，不是人工填写的。

## 1. 前置条件

构建机：

- Windows x64；
- .NET SDK `8.0.424`（ControlServer 的 `global.json` 强制此版本，`rollForward: disable`）。
  若未加入 `PATH`，把 `WIRE_TO_GATE_DOTNET_EXE` 指向该版本的 `dotnet.exe`；
- PowerShell 7（`pwsh`）；
- `git`，且能匿名或经认证读取上面三个仓库。

目标机（只运行、不构建）：

- Windows x64；
- **不需要** .NET SDK 或运行时：两端都以 self-contained 方式发布，运行时随包分发；
- 安装 ControlServer 需要管理员 PowerShell；
- 运行 OnboardHmi 需要交互式桌面会话（WPF 程序，不是服务）。

## 2. 构建发布候选

在 ControlServer 仓库的干净工作区中执行。`<onboard-commit>` 是要绑定的车载端 40 位 commit，
必须与 `release-manifest.json` 中记录的一致：

```powershell
.\scripts\New-WireToGateReleaseCandidate.ps1 `
    -OutputRoot <不存在的目录> `
    -OnboardCommit <onboard-commit>
```

脚本会：

1. 校验 ControlServer 工作区干净，取 `HEAD` 作为服务端身份；
2. 调 `scripts\Publish-ControlServer.ps1` 产出 self-contained 服务端包与逐文件 SHA-256 的
   `deployment-manifest.json`；
3. 把车载端仓库**克隆到 `<OutputRoot>-onboard-src`** 后按指定 commit 构建。车载端仓库对 agent
   只读，脚本因此从不写入已有的车载端工作区；
4. 车载端仓库没有 `global.json`，脚本在一次性克隆中写入 `8.0.424` 的固定值，并在 manifest 里以
   `sdkPinnedByReleaseScript` 记录这一事实；
5. 两端任一构建出现警告即失败退出（车载端 `TreatWarningsAsErrors=true`）；
6. 从服务端包的 `appsettings.json` **读回**协议身份，并要求 `approvalStatus` 为
   `APPROVED_RELEASE`；
7. 生成依赖与许可证清单、秘密扫描报告、`release-manifest.json` 与 `SHA256SUMS.txt`。

产物结构：

```text
<OutputRoot>/
├─ controlserver/                     服务端 self-contained 包（含 deployment-manifest.json）
├─ onboard-hmi/                       车载端 self-contained 包（含生产配置模板）
├─ scripts/                           安装、卸载、重建脚本
├─ inventory/                         依赖与许可证清单、秘密扫描报告
├─ RELEASE-CANDIDATE.md               本文件
├─ release-manifest.json              联合发布身份与逐文件 SHA-256
└─ SHA256SUMS.txt                     全部产物的 SHA-256
```

## 3. 校验产物

```powershell
Get-Content .\SHA256SUMS.txt | ForEach-Object {
    $parts = $_ -split '  ', 2
    if ($parts.Count -eq 2) {
        $actual = (Get-FileHash -LiteralPath $parts[1] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $parts[0]) { "MISMATCH $($parts[1])" }
    }
}
```

无输出即全部一致。`scripts\Install-ControlServerLocal.ps1` 在安装前还会独立校验服务端包的
`deployment-manifest.json`，逐文件比对 SHA-256 并拒绝任何越出包目录的路径。

## 4. 安装 ControlServer

### 4.1 外部秘密（不在包内、不进 Git、不进日志）

安装前必须已存在：

| 变量 | 作用域 | 用途 |
| --- | --- | --- |
| `CONTROL_SERVER_ONBOARD_CREDENTIAL` | Machine | 车载端会话凭据 |
| `CONTROL_SERVER_RIOT_CALL_API_KEY` | User | RIoT `CallApiKey` |
| `CONTROL_SERVER_MES_INGEST_SHARED_SECRET` | Machine | 仅当 MesIngest 为非 loopback 地址时必需 |

安装脚本把前两者写进服务专属的注册表 `Environment`（ACL 仅限 `SYSTEM` 与
`Administrators`），并且**不打印任何值**。

### 4.2 安装

管理员 PowerShell：

```powershell
.\scripts\Install-ControlServerLocal.ps1 `
    -PackagePath .\controlserver `
    -ResultPath <结果 JSON 路径> `
    -DiagnosticPath <诊断日志路径> `
    -CopyUserRiotSecretToMachine
```

`-CopyUserRiotSecretToMachine` 是显式授权，缺失即拒绝执行：它授权把用户作用域的 RIoT 凭据复制到
机器作用域。

脚本按顺序完成：备份既有数据根 → 复制包 → 生成自签根与 `localhost` 叶证书并导出 PFX 与 PEM →
注入秘密 → 写 `appsettings.Production.json` → 收紧安装目录与数据目录 ACL → 创建
`LocalSystem`／`Automatic` 服务 → **首启 → 停止 → 再启动 → 强制重启**，每次启动后做一次 HTTPS
存活检查 → 校验数据库与日志文件确已生成 → 写结果 JSON。

**健康检查不依赖系统信任存储**：脚本把本次安装生成的根证书导出为
`<DataRoot>\certs\localhost-development-root.pem`，并以 `curl --cacert` 钉住它验证链路。因此安装
过程不修改任何证书存储，也不会弹出信任确认对话框，可在非交互环境中完整跑完。若确实需要让本机
浏览器或其他工具直接信任该根证书，另加 `-InstallCurrentUserRoot`——它会把根证书导入
`CurrentUser\Root`，**该操作会弹出 Windows 安全确认对话框，只能在交互式会话中使用**。

任一步失败，脚本自动回滚：删服务、删安装目录、还原机器作用域环境变量、移除导入的根证书、
还原或删除数据根，并把回滚结果一并抛出。

### 4.3 隔离安装（不影响已有部署）

全部路径、服务名与端口都是参数，默认值即生产值。要在同一台机器上验证一个与生产部署完全隔离的
实例：

```powershell
.\scripts\Install-ControlServerLocal.ps1 `
    -PackagePath .\controlserver -ResultPath <结果 JSON> `
    -ServiceName '<另一个服务名>' `
    -InstallRoot '<另一个安装目录>' -DataRoot '<另一个数据目录>' -BackupRoot '<另一个备份目录>' `
    -OnboardPort <未占用端口> -HealthPort <未占用端口> `
    -SkipMachineEnvironmentInjection
```

`-SkipMachineEnvironmentInjection` 让隔离实例只写服务专属的注册表环境，不触碰机器作用域变量，
因此不会影响已在运行的生产服务；给了它就不再要求 `-CopyUserRiotSecretToMachine`。

## 5. 启动、停止、重启与健康检查

```powershell
Start-Service   -Name '8005 AGV ControlServer'
Stop-Service    -Name '8005 AGV ControlServer'
Restart-Service -Name '8005 AGV ControlServer' -Force
Get-Service     -Name '8005 AGV ControlServer'
```

HTTPS 端点（默认 `https://localhost:58007`，隔离实例用 `-HealthPort` 指定的端口）：

| 端点 | 含义 |
| --- | --- |
| `GET /health/live` | 进程存活。返回 `{"status":"live"}` |
| `GET /health/ready` | 业务就绪。数据库不可用返回 `503` + `DATABASE_UNAVAILABLE`；数据库可用但尚无完成五步恢复握手的车载会话返回 `503` + `RECOVERY_HANDSHAKE_REQUIRED`；就绪返回 `200` + `ready` |
| `GET /version` | 协议身份：tag、commit、manifest／schema／vectors 的 SHA-256、审批状态 |
| `GET /api/runtime/sessions` | 各车的会话代次与就绪原因 |

**首装后 `/health/ready` 返回 `503 RECOVERY_HANDSHAKE_REQUIRED` 是预期结果**，它证明数据库已迁移
且可读；只有车载端接入并完成五步恢复握手后才会转为 `ready`。

手工校验时钉住本次安装生成的根证书，不要用 `--insecure`：

```powershell
curl.exe --noproxy localhost --ssl-revoke-best-effort `
    --cacert '<DataRoot>\certs\localhost-development-root.pem' `
    'https://localhost:58007/health/live'
```

Windows 自带的 curl 使用 Schannel，`--cacert` 必须给 **PEM**（脚本导出的 `.pem`），DER 的 `.cer`
不被接受；`--ssl-revoke-best-effort` 用于跳过自签根证书无法完成的吊销查询。换成任何其他根证书，
这条命令都会以 `curl: (60)` 失败——这正是它构成校验而非摆设的原因。

## 6. 数据库初始化与迁移

- 存储是单文件 SQLite，默认 `<DataRoot>\data\controlserver.db`（安装脚本把绝对路径写进
  `appsettings.Production.json` 的 `ConnectionStrings:ControlServer`）；
- Host 在**每次启动**时执行 EF Core `Database.MigrateAsync()`，首启即建库建表，升级时自动补迁移。
  没有单独的迁移命令，也不需要外部数据库服务；
- 目录不存在时由 Host 自行创建；
- 数据根同时容纳 `certs\`（PFX 与导出的根证书公钥）与 `logs\`。

## 7. 日志

| 组件 | 路径 | 格式 |
| --- | --- | --- |
| ControlServer | `<DataRoot>\logs\controlserver-<yyyyMMdd>.ndjson` | Serilog Compact JSON，按天滚动，保留 14 份 |
| OnboardHmi | `<车载端安装目录>\logs\agv-<yyyyMMdd>.log` | 行文本，目录由 `logging.directory` 相对程序目录解析 |
| 安装过程 | `-DiagnosticPath` 指定的文件 | 每行一个带 UTC 时间戳的阶段标记 |

服务端的文件日志由安装脚本生成的 `appsettings.Production.json` 配置。产品默认配置只有 Console
sink——服务模式下控制台输出无处可去，**因此不要绕过安装脚本手工部署**，否则没有持久日志。

日志与安装结果 JSON 都不写入任何秘密值，只记录「是否存在」。

## 8. 运行 OnboardHmi

车载端是 WPF 交互程序，不是服务，需要交互式桌面会话：

```powershell
.\onboard-hmi\SQCD.Agv.Wpf.exe
```

配置注意事项：

- 程序**只读取自身目录下的 `appsettings.json`**，没有 `appsettings.<环境>.json` 分层覆盖。
  部署时必须用生产配置**整体替换** `appsettings.json`；
- 包内 `onboard-hmi\appsettings.Production.template.json` 已由构建脚本填入本包真实的车载端
  commit，其余 `REPLACE_*` 占位符是现场值，必须逐项替换后才能上线，至少包括 ControlServer 的
  IP、`serverCertificateSha256`、稳定的 `onboardInstanceId`、IO 模块 IP 与期望的 `vehicleKey`；
- 随包的开发默认 `appsettings.json` 里 `wireToGate.enabled=false`、`useTls=false`，且
  `onboardBuildCommit` 是仓库中的一个较早 commit，**不等于**本包的构建 commit。这个字段是握手时
  上报给服务端的**配置值**，不是二进制自身的身份：程序启动时写进日志的
  `version=<InformationalVersion>+<SourceRevisionId>` 才是，实测与本包构建 commit 一致。上线前
  必须把该配置字段改成真实 commit，`release-manifest.json` 的
  `components.onboardHmi.configuration.declaredBuildCommitMatchesBuild` 显式记录了这一差异；
- 车载端凭据同样从 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 注入，操作员标识从
  `CONTROL_SERVER_OPERATOR_ID` 注入。

## 9. 回滚与卸载

```powershell
.\scripts\Uninstall-ControlServerLocal.ps1 `
    -ServiceName '<服务名>' -InstallRoot '<安装目录>' -DataRoot '<数据目录>' `
    -ResultPath <结果 JSON> -TrustedRootThumbprint <安装结果里的指纹> `
    -ConfirmUninstall [-RemoveDataRoot]
```

- `-ConfirmUninstall` 是必需的显式授权；
- 目标若命中生产服务名或生产目录，还需要 `-AllowProductionService`，否则脚本拒绝执行——这是防止
  误删正在运行的生产部署的护栏；
- 不给 `-RemoveDataRoot` 时保留数据根（数据库、证书、日志），仅移除服务与安装目录，可用同一包
  重装；
- 结果 JSON 记录服务是否真的消失、目录是否真的删除、根证书移除了几张、生产服务是否仍在运行，
  以及卸载后仍在 LISTEN 的端口清单。

安装期回滚的备份位于 `<BackupRoot>\<runId>`，安装结果 JSON 的 `backupPath` 字段给出精确路径。

## 10. 依赖、许可证与秘密扫描

- `inventory\dependencies-controlserver.json` 与 `inventory\dependencies-onboard.json`：两端全部
  直接与传递依赖的精确版本，许可证从本机 NuGet 缓存的 `.nuspec` 读回（`license` 表达式或
  `licenseUrl`），并统计未能解析许可证的包数；
- `inventory\secret-scan.json`：对发布产物、发布脚本与两端源码执行的扫描。规则覆盖私钥块、
  PKCS#12 口令字面量、内联 API key／共享密钥／口令、Bearer 字面量、AWS access key id 与 GitHub
  token；同时按扩展名单独列出任何密钥材料文件（`.pfx`／`.p12`／`.pem`／`.key`／`.jks`／
  `.keystore`）。报告只记录规则名、路径与行号，**不记录命中的内容**。

## 11. 仍然阻断目标硬件与现场使用的外部条件

发布候选通过安装与生命周期验证，**不等于**它可以上车或进厂。以下条件与本包的构建、安装无关，
必须另行满足：

- **真实车辆动作需要逐次授权与现场物理安全 GO**，两者都要、每次都要、不可复用；本包的安装流程
  以 `JourneyRuntime.enabled=false` 部署，不建单、不动车；
- **RIoT 环境**：真实 RIoT 服务地址、`CallApiKey`、车辆 `vehicleKey`、`mapId`／`mapIdentity`、
  站点绑定与正数 `dispatchGeneration` 必须由现场提供并核验；RIoT owner 不提供技术支持，也不修改
  RIoT 程序；
- **MesIngest**：目标环境必须有可达的 MesIngest 实例；非 loopback 绑定必须配置共享密钥；
- **八仓 IO**：本轮以独立模拟器作为受控测试输入，**不构成**真实 IO 模块、接线、锁或光幕的资格；
  现场 IO 映射、反馈超时与 Modbus 地址需现场冻结；
- **TLS 身份**：安装脚本生成的是一次性的自签开发根与叶证书，仅用于本机回环验证。现场部署必须
  换成受控签发的证书，并把 `serverCertificateSha256` 同步进车载端配置；
- **车载端仓库对 agent 只读**，其产品代码与发布资产由车载端负责人维护。本发布候选只以精确
  commit、构建命令与产物 SHA-256 的形式登记车载端，不向该仓库写入任何内容；
- **协议仓库为审批门禁**，任何协议侧变更都需要两名负责人对同一具体变更明确批准。

## 12. 运行核心测试场景

**发布包里只有可运行的二进制，不含测试宿主。** 要跑核心场景，需要另外克隆源码仓库；下面的入口
不依赖本机已有的任何工作副本。

单元与集成测试（ControlServer 仓，需要 SDK `8.0.424`）：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```

逐切片 G2（绑定精确协议 manifest，`W2G-IS-00` 到 `W2G-IS-07`；`-Output` 必须是新目录）：

```powershell
.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice W2G-IS-00 `
    -ProtocolManifest <protocol 仓>\manifest\release.json -Output <新证据目录>
```

脚本先校验 manifest 的 SHA-256 与 `releaseVersion`／`protocolVersion`／schema／vectors 复合身份，
不匹配立即失败，因此不可能用错版本的协议凑出绿。

staged G3 向量（合成对端，无移动；runner 自行克隆四个仓库并绑定各自的精确 commit）：

```powershell
.\scripts\run-staged-g3.ps1 -StageRoot <不存在的短路径> -EvidenceRoot <新目录> `
    -InstallTemporaryCurrentUserRoot
.\scripts\run-staged-g3-restart.ps1 -StageRoot <不存在的短路径> -EvidenceRoot <新目录>
.\scripts\run-demand-bearing-g3-vectors.ps1 -StageRoot <不存在的短路径> -EvidenceRoot <新目录> `
    -FieldRunRoot <一次现场运行的 run 目录>
```

`run-staged-g3.ps1` 需要 Node.js 与 pnpm（协议 G1），并且要求显式的
`-InstallTemporaryCurrentUserRoot` 授权：它会向 `CurrentUser\Root` 装一张唯一的测试根证书、记录
指纹，并在 `finally` 中移除。另两个 runner 不需要该授权。

**这些场景的通过与否不改变当前的门禁状态**：W2G-IS-00～07 与 RC 目前仍为 `INCONCLUSIVE`，八类
G3 向量各有证据不等于八个切片通过。

## 13. 证据在哪里

- 构建产物身份：发布候选根目录的 `release-manifest.json` 与 `SHA256SUMS.txt`；
- 安装与生命周期验证：`evidence\rc\<日期>-<描述>\`，含安装结果 JSON、诊断日志与卸载结果 JSON；
- 协议一致性与切片证据：`evidence\` 下按门禁分目录，以及协议仓库自身的 `evidence\`；
- G3 向量证据：`evidence\g3\`，每个目录的 `SUMMARY.md` 给出断言逐条结果与绑定的精确 commit。
