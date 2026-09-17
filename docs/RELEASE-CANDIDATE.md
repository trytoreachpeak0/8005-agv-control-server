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
   `deployment-manifest.json`；再从同一 commit 发布 self-contained 看板包（`dashboard/`，自带
   `deployment-manifest.json`），看板出厂设置不是绑 `127.0.0.1` 即失败退出（control-server#80）；
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
├─ dashboard/                         看板 self-contained 包（含 deployment-manifest.json，只读、绑 127.0.0.1）
├─ onboard-hmi/                       车载端 self-contained 包（含生产配置模板）
├─ scripts/                           安装、卸载、升级、重建脚本
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

脚本按顺序完成：备份既有数据根 → 复制包 → 创建数据根 → 注入秘密 → 写
`appsettings.Production.json` → 收紧安装目录与数据目录 ACL → 创建 `LocalSystem`／`Automatic`
服务 → **首启 → 停止 → 再启动 → 强制重启**，每次启动后做一次 HTTP 存活检查 → 校验数据库与日志
文件确已生成 → 写结果 JSON。

**本版本不生成、不分发、不导入、不续期任何证书。** 两条链路——Onboard NDJSON（TCP 58005）与车辆
安全投影（Kestrel 上的 HTTP，默认 58007）——都是明文。安装过程不读写任何证书存储，不弹出信任确认
对话框，可在非交互环境中完整跑完；健康检查直接读 `http://<HealthBindAddress>:<HealthPort>/health/live`
的 body，既不需要 `--cacert`，也不需要 `--insecure`。

监听地址是参数，**默认仍为 `127.0.0.1`**：

| 参数 | 作用 | 默认 |
| --- | --- | --- |
| `-ListenAddress` | Onboard NDJSON 监听地址（写进 `OnboardTransport:listenAddress`） | `127.0.0.1` |
| `-HealthBindAddress` | Kestrel 的**唯一**绑定地址，`/health/*`、`/version` 与车辆安全投影都在其上 | `127.0.0.1` |

异机部署必须显式传入，见 4.4。

任一步失败，脚本自动回滚：删服务、删安装目录、还原机器作用域环境变量、还原或删除数据根，并把
回滚结果一并抛出。

**看板（可选，control-server#80）。** 加 `-DashboardPackagePath .\dashboard` 时，服务生命周期检查通过后
脚本再装看板：校验看板包的 `deployment-manifest.json` 且要求与服务端包同一 commit → 复制到
`-DashboardInstallRoot`（默认 `C:\Program Files\8005 AGV\ControlServer.Dashboard`）并收紧 ACL → 注册
`LocalSystem` 开机计划任务 `-DashboardTaskName`（默认 `8005 AGV ControlServer Dashboard`，失败自动重启、
不限时长）→ 启动并确认 `http://127.0.0.1:<DashboardPort>/`（默认 58009）渲染出旅程阻断卡片。看板用计划任务
而不是 Windows 服务，是因为看板进程本身不接 Windows 服务宿主，而接入要改的正是看板主文件。

看板**无认证、只绑 `127.0.0.1`**，与 `-HealthBindAddress` 无关；它经 `-HealthBindAddress` 那个地址读服务端的
`/api/dashboard/` 只读端点。维护管理员远程查看用 `ssh -L 58009:127.0.0.1:58009 <服务器>`。对局域网开放是另一次
决定，不在本版本。旅程阻断卡片的两道升级线（默认 10 分钟、30 分钟）在服务端包的
`blocked-journey-escalation.settings.json` 里，不在 `appsettings.json`。卸载时把同样的任务名与安装目录传给
`Uninstall-ControlServerLocal.ps1` 的 `-DashboardTaskName`／`-DashboardInstallRoot`；不传则不动看板。

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

### 4.4 异机（非 loopback）明文部署

车载端在另一台机器上时，两个监听地址都必须改到车载端够得着的地址上——默认的 `127.0.0.1` 只服务同机：

```powershell
.\scripts\Install-ControlServerLocal.ps1 `
    -PackagePath .\controlserver `
    -ResultPath <结果 JSON 路径> `
    -DiagnosticPath <诊断日志路径> `
    -ListenAddress 192.168.200.1 -HealthBindAddress 192.168.200.1 `
    -CopyUserRiotSecretToMachine
```

- 两个地址既可以是具体网卡 IP，也可以是通配 `0.0.0.0`。给通配时脚本自己的生命周期检查改拨
  `127.0.0.1`——没有任何客户端连得上通配地址本身；
- 实际形态以**安装结果 JSON 回读的字段**为准，不以命令行为准：`onboardTransportEndpoint`
  （`tcp://<地址>:<端口>`）、`httpEndpoint`、`httpCheckOrigin` 与 `transport: "plaintext"`；
- 防火墙：目标机需放行 **TCP `-OnboardPort`（默认 58005）** 与 **TCP `-HealthPort`（默认 58007）**。
  这两个端口现在承载明文业务与明文投影，放行范围就是暴露范围；
- **判对端可达只能用返回 body 的往返。** 装有全局代理（例如 Clash 全局模式）的机器上，ping 与 TCP
  connect 对任意主机、任意端口乃至不存在的主机都会「成功」。明文形态下没有 TLS 握手失败兜底，连错
  主机可能表现为静默挂起而不是报错。一律用带 `--noproxy` 的 `curl.exe` 读 body 判定：

```powershell
curl.exe --noproxy 192.168.200.1 --max-time 10 'http://192.168.200.1:58007/health/live'
```

- 车载端侧的对应项见第 8 节：`wireToGate.host` 指向 `-ListenAddress`，
  `vehicleSafety.endpoint` 指向 `http://<HealthBindAddress>:<HealthPort>/api/onboard/v1/vehicle-safety`。

### 4.5 从证书版本升级已有安装

`scripts\Update-ControlServerLocal.ps1` 就地升级已有安装。**不带任何目标参数时升级的就是生产安装**
（服务名 `8005 AGV ControlServer`、`C:\Program Files\8005 AGV\ControlServer`、
`C:\ProgramData\8005\ControlServer`、`C:\ProgramData\8005\ControlServer-backups`、机器级变量
`CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD`），日常升级照下面这条命令跑即可：

```powershell
.\scripts\Update-ControlServerLocal.ps1 `
    -PackagePath <新包目录> -ResultPath <新结果 JSON> `
    -DiagnosticPath <诊断日志路径> -VerifySafetyProjectionReadOnly
```

五个目标参数 `-ServiceName` / `-InstallRoot` / `-DataRoot` / `-BackupRoot` /
`-CertificatePasswordVariable` 存在的唯一目的是**先在隔离实例上排练一遍这条升级路径**再动生产：
给全五个就完全不碰生产的服务、目录与机器级变量。默认值即上面括号里的生产值，因此省略它们与旧版本
硬编码的行为逐字相同。

升级器停服 → 备份安装目录与完整数据根 → 把保留的 `appsettings.Production.json` 迁移成明文键集 →
清除证书遗留物 → 换二进制 → 起服并回读 `/health/live`、`/version` 与（给了开关时）只读投影；任一步
失败即回滚二进制、SQLite 与被清除的机器级变量。

脚本自动完成的三件事，逐项记录在结果 JSON 的 `certificateRemoval` 段：

1. 从保留的生产配置中删除 `OnboardTransport:serverCertificatePath`、
   `OnboardTransport:serverCertificatePasswordEnvironmentVariable`、
   `OnboardTransport:allowInsecureLoopback`、`OnboardSafetyProjection:requireHttps`。新二进制在启动期
   **显式拒绝**这四个键中的任意一个，因此不迁移的升级会启动失败并整体回滚，而不是带着死配置跑起来；
2. 把 `Health:url` 的 `https://` 改写成 `http://`；
3. 删除 `<DataRoot>\certs\` 与机器作用域环境变量 `CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD`。

**脚本不做、必须人工做的一件事**：当初经 `-InstallCurrentUserRoot` 导入 `CurrentUser\Root` 的那张
自签根证书。它落在**执行安装的那个用户账户**作用域下，升级以服务账户视角运行时够不着，所以脚本不碰
它，并在结果 JSON 里如实记为 `currentUserRootCertificateRemoved: false` /
`currentUserRootCertificateRemovalIsManual: true`。`certificateDirectoryRemoved: true`
**不代表**这张根证书已经不在了。

指纹来源，按顺序取第一个可得的：

- 当初那次安装的结果 JSON（`schemaVersion: 1`）里的 `certificate.trustedRootThumbprint`；
- 旧结果 JSON 已丢失时，按主题名查——该根证书的主题固定以这串前缀开头：

```powershell
Get-ChildItem Cert:\CurrentUser\Root |
    Where-Object { $_.Subject -like '*8005 AGV ControlServer Local Development Root*' } |
    Select-Object Subject, Thumbprint, NotAfter
```

在**当初执行安装的那个用户账户**下删除，并回读确认：

```powershell
Remove-Item -LiteralPath 'Cert:\CurrentUser\Root\<thumbprint>'
Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq '<thumbprint>' }
```

第二条命令**无输出**才算删掉。只删上面两种来源确认过的那一张，不要按主题名批量删除——同一存储里的
其他根证书与本产品无关。

## 5. 启动、停止、重启与健康检查

```powershell
Start-Service   -Name '8005 AGV ControlServer'
Stop-Service    -Name '8005 AGV ControlServer'
Restart-Service -Name '8005 AGV ControlServer' -Force
Get-Service     -Name '8005 AGV ControlServer'
```

HTTP 端点（默认 `http://127.0.0.1:58007`；地址由 `-HealthBindAddress`、端口由 `-HealthPort` 决定，
以安装结果 JSON 的 `httpEndpoint` 为准）：

| 端点 | 含义 |
| --- | --- |
| `GET /health/live` | 进程存活。返回 `{"status":"live"}` |
| `GET /health/ready` | 业务就绪。数据库不可用返回 `503` + `DATABASE_UNAVAILABLE`；数据库可用但尚无完成五步恢复握手的车载会话返回 `503` + `RECOVERY_HANDSHAKE_REQUIRED`；就绪返回 `200` + `ready` |
| `GET /version` | 协议身份：tag、commit、manifest／schema／vectors 的 SHA-256、审批状态 |
| `GET /api/runtime/sessions` | 各车的会话代次与就绪原因 |

**首装后 `/health/ready` 返回 `503 RECOVERY_HANDSHAKE_REQUIRED` 是预期结果**，它证明数据库已迁移
且可读；只有车载端接入并完成五步恢复握手后才会转为 `ready`。

手工校验：

```powershell
curl.exe --noproxy 127.0.0.1 --max-time 10 'http://127.0.0.1:58007/health/live'
```

`--noproxy` 不是可选的排版：装有全局代理的机器上，省掉它会让请求被代理接管，从而对**任意**主机与
端口都返回「成功」，校验失去意义。`--max-time` 同理——明文形态下连错主机可能静默挂起而不是报错。
判定标准是**读回的 body**（`{"status":"live"}`），不是退出码为 0，更不是 ping 通。

## 6. 数据库初始化与迁移

- 存储是单文件 SQLite，默认 `<DataRoot>\data\controlserver.db`（安装脚本把绝对路径写进
  `appsettings.Production.json` 的 `ConnectionStrings:ControlServer`）；
- Host 在**每次启动**时执行 EF Core `Database.MigrateAsync()`，首启即建库建表，升级时自动补迁移。
  没有单独的迁移命令，也不需要外部数据库服务；
- 目录不存在时由 Host 自行创建；
- 数据根下只有 `data\` 与 `logs\`。本版本没有 `certs\`；升级已有安装时该目录由
  `Update-ControlServerLocal.ps1` 删除（见 4.5）。

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
  commit，其余 `REPLACE_*` 占位符是现场值，必须逐项替换后才能上线：`REPLACE_CONTROL_SERVER_IP`
  （`wireToGate.host`）、`REPLACE_CONTROL_SERVER_HOST`（`vehicleSafety.endpoint` 里的 `主机:端口`）、
  `REPLACE_WITH_STABLE_UUID`、`REPLACE_WITH_40_CHARACTER_GIT_COMMIT`、`REPLACE_RULE_SERVER_IP`、
  `REPLACE_IO_MODULE_IP` 与 `REPLACE_WITH_EXPECTED_VEHICLE_KEY`。**本版本的模板里没有
  `serverCertificateSha256`，也没有 `useTls`**——这两个键已从车载端产品代码中移除；
- `vehicleSafety.enabled` 必须保持 `true`，`vehicleSafety.endpoint` 必须是**明文 HTTP** 的完整 URI，
  指向服务端 `-HealthBindAddress`／`-HealthPort` 的绑定，例如
  `http://192.168.200.1:58007/api/onboard/v1/vehicle-safety`。车载端只接受 `http` scheme（
  `VehicleSafetySettings.Validate` 硬校验），填 `https://` 会直接启动失败；不需要导入、分发或信任
  任何证书；
- **车载端拒绝残留的 TLS 期配置键**：`wireToGate` 下出现 `useTls` 或 `serverCertificateSha256` 时
  启动即抛 `WIRE_TO_GATE配置键<键名>已移除，当前版本固定使用明文TCP/HTTP传输。`。从旧版本的配置
  文件改写而来时，删键，不要把值改成 `false` 或空串——判据是键名存在与否，不是值；
- 启动车载端前，以同一车载凭据只读调用 `vehicleSafety.endpoint`。只有 HTTP 200、`vehicleKey` 与
  `vehicleSafety.expectedVehicleKey` 精确一致、`motionState=STOPPED` 且 `observedAt` 未超出
  `maximumEvidenceAgeMs`，干净会话才应进入 `Ready`；`UNKNOWN`／`MOVING` 或过期证据保持
  `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` 是安全闸门的预期行为；
- 随包的开发默认 `appsettings.json` 里 `wireToGate.enabled=false`、`vehicleSafety.enabled=false`
  （`endpoint` 是不可解析的 `http://control.example.invalid/...`），且
  `onboardBuildCommit` 是仓库中的一个较早 commit，**不等于**本包的构建 commit。这个字段是握手时
  上报给服务端的**配置值**，不是二进制自身的身份：程序启动时写进日志的
  `version=<InformationalVersion>+<SourceRevisionId>` 才是，实测与本包构建 commit 一致。上线前
  必须把该配置字段改成真实 commit，`release-manifest.json` 的
  `components.onboardHmi.configuration.declaredBuildCommitMatchesBuild` 显式记录了这一差异；
- 车载端凭据同样从 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 注入，操作员标识从
  `CONTROL_SERVER_OPERATOR_ID` 注入。

车载端日志出现 `readiness=RecoveryRequired` 时，先查 `GET /api/runtime/sessions` 的 `reasonCode`。
`DEPARTURE_SAFETY_NOT_READY` 已证明表示恢复报告已收到、但可信车辆停稳事实未满足；它不是
`RecoveryStateReport` 漏发。只有 `HANDSHAKE_INCOMPLETE` 才继续检查五步握手消息。

### 8.1 新旧两端错配：错误文本对照表

**两端必须同版本升级。** 明文端与 TLS 期端之间没有协商、没有降级、没有自动探测：错配的结果是连接
建立不起来，而两端给出的文本**都不含 TLS 字样**，现场极易误判成「网络不通」。下表的文本是实测逐字
记录（跨机取证，非推断）：

**方向 A — TLS 期车载端（配置里有 `useTls=true`）连本版本的明文服务端**

| 侧 | 逐字文本 |
| --- | --- |
| 车载端 | `上层会话不可用：Received an unexpected EOF or 0 bytes from the transport stream.。将在2秒后重连。` |
| 服务端 | `Onboard connection ended with a protocol or transport error.` |

服务端的 `SessionHello` 计数**不增加**——没有任何消息进入协议层。车载端**无限重连、不退出**，是静默
故障形态：界面看起来在「重连中」，实际上永远连不上。

**方向 B — 本版本的明文车载端连 TLS 期服务端**

| 侧 | 逐字文本 |
| --- | --- |
| 车载端 链路 A（NDJSON） | `上层会话不可用：ControlServer在会话恢复期间关闭了连接。。将在2秒后重连。` |
| 车载端 链路 B（投影） | `An error occurred while sending the request.` |
| 服务端 | `System.Security.Authentication.AuthenticationException: Cannot determine the frame size or a corrupted frame was received.` |

**方向 B 的车载端措辞是全表最危险的一条**：「ControlServer 在会话恢复期间关闭了连接」听起来像业务层
的恢复问题，与传输形态毫无关系。两个方向里，唯一点出真因的都是**服务端**日志
（`<DataRoot>\logs\controlserver-<yyyyMMdd>.ndjson`）。因此：车载端反复重连而服务端 `SessionHello`
不增加时，先读服务端日志，再怀疑网络。

排除顺序建议：

1. 服务端日志有 `AuthenticationException` → 服务端还是 TLS 期版本，升级服务端；
2. 服务端日志有 `Onboard connection ended with a protocol or transport error.` 且
   `SessionHello` 计数不增加 → 车载端还是 TLS 期版本，或其配置仍带 `useTls`；
3. 服务端日志里这条连接**根本没有出现** → 才是真正的网络或地址问题，按 4.4 用带 `--noproxy` 的
   `curl.exe` 读 body 逐段验证。

## 9. 回滚与卸载

```powershell
.\scripts\Uninstall-ControlServerLocal.ps1 `
    -ServiceName '<服务名>' -InstallRoot '<安装目录>' -DataRoot '<数据目录>' `
    -ResultPath <结果 JSON> `
    -ConfirmUninstall [-RemoveDataRoot]
```

- `-ConfirmUninstall` 是必需的显式授权；
- 目标若命中生产服务名或生产目录，还需要 `-AllowProductionService`，否则脚本拒绝执行——这是防止
  误删正在运行的生产部署的护栏；
- 本版本的卸载脚本**没有** `-TrustedRootThumbprint`，也不触碰任何证书存储：没有证书可移除。从证书
  版本升级上来的机器若还留着 `CurrentUser\Root` 里的旧自签根，按 4.5 的人工步骤删；
- 不给 `-RemoveDataRoot` 时保留数据根（数据库与日志），仅移除服务与安装目录，可用同一包重装；
- 结果 JSON 记录服务是否真的消失、目录是否真的删除、生产服务是否仍在运行，以及卸载后仍在 LISTEN
  的端口清单。

安装期回滚的备份位于 `<BackupRoot>\<runId>`，安装结果 JSON 的 `backupPath` 字段给出精确路径。

## 10. 依赖、许可证与秘密扫描

- `inventory\dependencies-controlserver.json` 与 `inventory\dependencies-onboard.json`：两端全部
  直接与传递依赖的精确版本，许可证从本机 NuGet 缓存的 `.nuspec` 读回（`license` 表达式或
  `licenseUrl`），并统计未能解析许可证的包数；
- `inventory\secret-scan.json`：对发布产物、发布脚本与两端源码执行的扫描。规则覆盖私钥块、
  PKCS#12 口令字面量、内联 API key／共享密钥／口令、Bearer 字面量、AWS access key id 与 GitHub
  token；同时按扩展名单独列出任何密钥材料文件（`.pfx`／`.p12`／`.pem`／`.key`／`.jks`／
  `.keystore`）。报告只记录规则名、路径与行号，**不记录命中的内容**。

这两份清单是**闸门，不只是记录**。`New-WireToGateReleaseCandidate.ps1` 在写完
`inventory\` 之后、生成 `release-manifest.json` 之前调用 `Assert-ReleaseScanGate`，命中以下任一条
即中止，不产出 manifest、不产出 `SHA256SUMS.txt`、不产出可宣称通过的包：

- 任何扫描 finding；
- 任何密钥材料文件；
- 任何不在允许清单内的包解析不出许可证。允许清单当前只有 `RIoT.Sdk.Core`、`RIoT.Sdk.Facade`、
  `RIoT.Sdk.Generated` 三个本项目自建包，逐个具名——**新**出现的无许可证依赖会让发布失败，而不是
  悄悄并进计数。清单与实际的 NuGet 全局包根写入 manifest 的 `inventory.scanGate`。

失败时 `inventory\` 已经落盘，可据以定位；失败信息只给「路径:行号:规则名」，不回显命中内容。
许可证从 `NUGET_PACKAGES`（未设时为 `%USERPROFILE%\.nuget\packages`）读回。

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
- **受控的厂内网是本版本的部署前提**：两条链路都是明文，安全性完全依赖网络本身受控。下面第 11.1
  节的三条限制在不可信网络下都会成为真实风险；
- **车载端仓库对 agent 只读**，其产品代码与发布资产由车载端负责人维护。本发布候选只以精确
  commit、构建命令与产物 SHA-256 的形式登记车载端，不向该仓库写入任何内容；
- **协议仓库为审批门禁**，任何协议侧变更都需要两名负责人对同一具体变更明确批准。

### 11.1 明文传输的已知限制

本版本把 OnboardHmi ↔ ControlServer 的两条链路从 TLS/HTTPS 改为明文 TCP/HTTP，并移除了整套证书
机制。下面三条是这一选择的**已知代价**，由项目负责人于 2026-08-31 在知情前提下接受，前提是部署在
受控的工厂内网。它们不是待修缺陷，也不应在部署时被当作「以后再说」：

- **车载凭据以明文经网络传输。** `credentialProof` 是协议必填字段，车载端把静态共享密钥原样放进
  `SessionHello` 的 payload。链路明文之后，**在网络上抓一次包即可永久冒充该车载端**——密钥是静态的，
  不轮换、不挑战应答，重放没有时间窗限制。凡是能接触到这段网络的人或设备，都等价于持有该凭据。
- **安全闸门的输入变得可篡改。** `motionState`、`observedAt` 等车辆安全投影字段经明文 HTTP 传输，
  中间人可以改写。已验证过的「移动中拦、停稳放行」这一安全行为，其成立**前提是网络可信**；在不可
  信网络下，攻击者可以把 `MOVING` 改成 `STOPPED` 来诱使闸门放行。这是 safety 层面的后果，不只是
  security 层面的。
- **健康、版本端点随投影一并暴露。** Kestrel 只有一个绑定，`/health/live`、`/health/ready`、
  `/version`、`/api/runtime/sessions` 与 `/api/onboard/v1/vehicle-safety` 都挂在其上。异机部署要求
  投影对车载端可达，因此 `-HealthBindAddress` 必须绑非 loopback，这些端点也就一并暴露到厂内网。
  本版本**有意不加**端点级过滤或来源 IP 白名单：前者是往一个以「删机制」为目标的版本里新加机制，
  后者挡不住能抓包的人，属安慰剂。

这三条的适用前提是**受控的工厂内网**。若部署环境不满足这一前提，正确的做法是先解决网络隔离，而不是
在本版本上叠加补偿措施。

## 12. 运行核心测试场景

**发布包里只有可运行的二进制，不含测试宿主。** 要跑核心场景，需要另外克隆源码仓库；下面的入口
不依赖本机已有的任何工作副本。

单元与集成测试（ControlServer 仓，需要 SDK `8.0.424`）：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```

逐切片 G2（绑定精确协议 manifest，`FP-IS-00` 到 `FP-IS-07`；`-Output` 必须是新目录）：

```powershell
.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice FP-IS-00 `
    -ProtocolManifest <protocol 仓>\manifest\release.json -Output <新证据目录>
```

脚本先校验 manifest 的 SHA-256 与 `releaseVersion`／`protocolVersion`／schema／vectors 复合身份，
不匹配立即失败，因此不可能用错版本的协议凑出绿。

staged G3 向量（合成对端，无移动；runner 自行克隆四个仓库并绑定各自的精确 commit）：

```powershell
.\scripts\run-staged-g3.ps1 -StageRoot <不存在的短路径> -EvidenceRoot <新目录>
.\scripts\run-staged-g3-restart.ps1 -StageRoot <不存在的短路径> -EvidenceRoot <新目录>
.\scripts\run-demand-bearing-g3-vectors.ps1 -StageRoot <不存在的短路径> -EvidenceRoot <新目录> `
    -FieldRunRoot <一次现场运行的 run 目录>
```

`run-staged-g3.ps1` 需要 Node.js 与 pnpm（协议 G1）。三个 runner 都走明文，**都不再需要
`-InstallTemporaryCurrentUserRoot`**（该参数已随证书机制一并移除），也都不向任何证书存储写入，因此
都可无人值守运行。三者共用 `run-staged-g3.ps1` param 块里的四个 commit 绑定，另两个 runner 从中回读
而不是各自重述。脚本内部仍有 `StagedG3TlsHarness` 这类 TLS 期的**命名**残留，是历史名称，不代表行为。

**这些场景的通过与否不改变当前的门禁状态**：`W2G-IS-00`～`07` 与 RC 当时即记作 `INCONCLUSIVE`，
八类 G3 向量各有证据不等于八个切片通过。**切到协议 v2 之后这句话只会更硬**：那些证据绑定的是
`protocol-v0.1.1` 的 manifest 哈希，服务端已经不再发送它，所以 `FP-IS-00`～`07` 在 v2 下**一条
都还没有 G2 或 G3 结论**——不是继承了 `INCONCLUSIVE`，是重新开始。重证由票 17 负责。

## 13. 证据在哪里

- 构建产物身份：发布候选根目录的 `release-manifest.json` 与 `SHA256SUMS.txt`；
- 安装与生命周期验证：`evidence\rc\<日期>-<描述>\`，含安装结果 JSON、诊断日志与卸载结果 JSON；
- 协议一致性与切片证据：`evidence\` 下按门禁分目录，以及协议仓库自身的 `evidence\`；
- G3 向量证据：`evidence\g3\`，每个目录的 `SUMMARY.md` 给出断言逐条结果与绑定的精确 commit。
