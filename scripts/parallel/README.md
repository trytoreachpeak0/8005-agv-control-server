# scripts/parallel —— 并行期 v2 在 factory01 上的第二套部署

control-server#262。约 2026-10-08 起 `factory01` 上同时跑两套 ControlServer：MVP 驱动 `agv01`，
这一套驱动 `agv02`／`agv03`，只吃注入的测试需求。权威文档是 `8005-workspace` 仓的
`remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md`，控制端入口是同目录下的
`scripts/19-deploy-control-server-parallel.ps1`。这里只放随产品版本走的部分。

## ⚠️ 出厂的实例定义故意装不上：两个 map 26 的值还是占位

用户 2026-09-21 答复：**MVP 跑 map 25，v2 跑 map 26**。

| 键 | 现值 | 出处 |
| --- | --- | --- |
| `routeGraph.mapId`、`journeyRuntime.mapId` | 26 | 用户答复 |
| `journeyRuntime.mapIdentity` | `老厂前线new_wk` | `evidence/field/2026-09-19-B6-map-name-baseline-check/real-riot/fields.json` |
| `journeyRuntime.dispatchZone`、`allowedDispatchZones` | `REPLACE_WITH_MAP26_DISPATCH_ZONE` | **待现场取回**：形式可推，没人确认现场真这么叫 |
| `journeyRuntime.admissionPolicyDeploymentId` | `REPLACE_WITH_MAP26_ADMISSION_POLICY_DEPLOYMENT_ID` | **待现场取回**：无出处，日期是一次现场标定的产物 |
| 站点清单 `task-type-stations.settings.json`（在包里，不在定义里） | 绑 25 | 要出 26 版 |

校验见到 `REPLACE_` 就拒绝，所以**在从 factory01 直查 RIoT 把这两个值取回之前，这份定义过不了校验、装不上**——这是刻意的。另外 map 25、`老厂前线new`、任何 `MAP-25-*` 标识符也一律拒绝：以前「按原样装会指向 MVP 那张图」只写在文档里，现在是一条检查（control-server#262 复审 M3）。

## 清桩出口的两节配置与恢复凭据由部署链写，不再手工合入（control-server#454）

人工清桩出口要两节配置：`VehicleFaultRecovery`（Host 恢复入口开关与凭据变量名）和 `FieldOperatorRoles`（名单路径、车载端入口声明）。
以前部署链不写这两节，只能装完手工合入；首装会整份重写 `appsettings.Production.json`、重建服务的 `Environment`，手工合入的东西
就被静默冲掉，服务端只告警 2271/2272、照旧 `ORDER_HANG`。现在：

- 实例定义里必须显式写 `vehicleFaultRecovery.enabled` 与 `fieldOperatorRoles.{path, onboardClearanceEntryDeclared}`，缺了就拒绝。
  `path` 必须在 `opsRoot` 里（装包会替换安装目录）。凭据变量名**不由定义给**，覆盖层固定写 `CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL`，
  定义里写 `credentialEnvironmentVariable` 会被拒——免得并行实例指到 MVP 的变量。出厂定义是第一阶段：入口关、未声明车载端入口。
- 首装、升级、回滚三条路径都跑同一个 `Set-InstanceConfiguration`：合并覆盖层并回读核对覆盖层写的**每一个**值；把凭据写进服务的
  `Environment`；名单文件不存在就建一个空名单（`{"operators":[]}`，服务端读作「没人有权限」，安全方向），已有的绝不覆盖。
- 凭据的值：安装与升级取 `deploy-config.json` 的 `faultRecoveryCredential`（控制端从 DPAPI 秘密存储填），没有就沿用服务里已有的那一条；
  回滚没有 `deploy-config.json`，只沿用已有的。**在产品脚本运行之前**就读，因为首装会重建 `Environment`。入口开着而两处都没有，
  在停任何东西之前拒绝。
- 结束时打一行 `CLEARANCE_EXIT_READINESS ...`，按服务端 `StationClearanceExit` 的规则读生效配置、`Environment` 里的变量名（不读值）
  和名单文件。两节缺失、或入口开着却没有凭据：抛 `CLEARANCE_EXIT_BROKEN`，安装算失败。出口不可用（例如第一阶段名单为空）：
  打 `CLEARANCE_EXIT_UNAVAILABLE` 警告并列原因码，不算失败。它读的是服务将读的文件，不是运行中进程的判定，服务端没有对外暴露这个状态。

## 升级与回滚前先把旅程运行时关掉（control-server#454）

产品升级脚本 `Update-ControlServerLocal.ps1` 的预检要求已装配置里 `JourneyRuntime.enabled` 为 false（`ae2f99be9` 起）：
它会用保留下来的配置拉起还没验证过的新版本，做启动、存活检查、重启、再检查，结果文件写 `journeyRuntimeEnabled=false`、
`vehicleMoved=false`；运行时开着，新版本就会在这次检查里取需求、建单。并行实例的覆盖层写的是 true，所以首装之后的升级和
`-Rollback` 以前都被这道预检拒掉。

现在升级分支走 `ParallelHost.psm1` 的 `Invoke-ParallelProductUpgrade`，顺序是：

1. **派车闸门开着就拒绝**（`UPGRADE_REFUSED_DISPATCH_OPEN`）。已装配置里 `RiotCreateDispatch.enabled` 为真时，车可能在途，
   停服务等于中途停掉运行时对它的故障监看。安装脚本在最开头就查一次（在记录定义、回滚对调目录、解包之前），包装函数停服务前
   再查一次。要升级或回滚，先把已装 `appsettings.Production.json` 里的 `RiotCreateDispatch.enabled` 改成 false 并重启服务，
   等 `agv02`／`agv03` 的单都 `Completed`，再跑。
   同一个开头检查（`Get-ParallelPreInstallRefusal`）还按失败即关拒绝两种说不清的状态：服务在而已装配置不在
   （`INSTALLED_CONFIGURATION_MISSING`），或配置读不出来（`INSTALLED_CONFIGURATION_UNREADABLE`）。以前这次检查只在配置文件存在时
   才跑，`-Rollback` 会先对调包目录再失败。服务不在（首装）时不查。
2. 停服务，再把文件里的开关置为 false，然后调升级脚本。于是升级的检查在运行时关着时进行，它的备份和失败回退也都停在 false。
3. 升级成功后，`Set-InstanceConfiguration` 的覆盖层才把它写回定义里的值。

升级失败时开关留在 false（安全方向），打 `JOURNEY_RUNTIME_LEFT_DISABLED` 警告，写明文件里的值和服务状态，原样抛出。失败后
服务处在哪种状态取决于升级脚本在哪一步失败：在它自己的预检里失败（例如包清单不对、输出路径已存在），服务停在我们停下的状态；
预检之后再失败，升级脚本自己的回退会用**旧二进制加 false** 把服务拉起来。两种情况实例都不派车。

**恢复办法是重新部署当前在跑的那个 commit（或修好的新包），不要用 `-Rollback` 来恢复开关。**安装模式失败时代际对调还没发生，
这时再跑 `-Rollback`，装上的是 `.previous`，也就是更早一代，车载端版本可能对不上。

## 路径和名字只认一种写法

control-server#262 复审找到过一个严重缺陷：卸载脚本在一种很常见的写错下（JSON 里用正斜杠写路径）
会删掉 MVP 的安装根和生产数据库目录。修法是白名单，不是补黑名单：

- **路径**必须是规范写法——盘符开头、全反斜杠、没有 `.`／`..` 段、没有 `~` 短名、不是 UNC 或
  `\\?\`、每段不以点或空格结尾——而且必须**直接**位于 `C:\Program Files\8005 AGV`、`C:\ProgramData\8005`、
  `D:\zhengyushao` 三者之一下面，**目录名里带 `V2` 标记**（`.V2`、`-v2-` 这种，前后是分隔符）。
- **服务名和计划任务名**不许有 `* ? [ ]`，也必须带 `V2` 标记。`Get-Service`、`Stop-Service`、
  `Get-ScheduledTask` 都会展开通配符。
- 生产路径清单还留着，作第二道；但它必然不全，所以第一道是白名单——白名单漏了，后果是「删不掉」，
  黑名单漏了，后果是「删错了」。

卸载时服务那一步只要失败（尤其是产品卸载脚本以 production 拒绝），整个卸载立即中止，一个目录都不删。
「失败」不是靠识别各种失败方式判断的：产品卸载脚本可以不抛异常就失败（`exit 1`、`Continue` 下的
`Write-Error`、原生命令的退出码），所以 `Invoke-ParallelProductUninstaller` 反过来要求**正面确认成功**——
退出码为 0、这一次新写出的结果文件里是 `PASS` 且服务名对得上、服务确实已经不在了。缺一样就当失败。
结果文件名带 GUID，别的进程猜不到。这个确认成立还有一个前提：产品卸载脚本开头就设了
`$ErrorActionPreference = 'Stop'`，而且只在末尾写一次 `PASS`。自测对这两点有一道防回归检查，但它只认得四种违反
写法（第一句不是设 `Stop`、`PASS` 出现次数不是一次、`PASS` 不在最后三句、有 `trap`），认不出之后再改回 `Continue`、
`$PSDefaultParameterValues`、用 try/catch 吞掉错误、把 `PASS` 拆开拼接这些。产品脚本一改，要人重读一遍。
同样的检查在卸载时也会对**实际要调用的那一份**产品脚本再做一次（通常是已装包或上一代包自带的那份，不是仓库里的），
不过就中止，由 `Get-ParallelProductUninstallerPath` 负责。

关于 `sc.exe delete` 返回 1072（服务已被标记为待删除）：复审担心这会让退出码非 0、造成误报。实测不会
（`evidence/deploy/20260921-cs262-parallel-instance/review8-sc1072-probe.txt`）：产品脚本在 `sc.exe` 之后还会跑一次
`netstat.exe`，调用方拿到的是最后一个原生命令的退出码 0。这依赖两条命令的先后；哪天 `netstat` 被删掉或挪到
`sc.exe` 前面，1072 就会被判为失败、卸载中止——方向是安全的，重跑一次即可。

卸载脚本应当只经由 `Invoke-ParallelProductUninstaller` 调用产品脚本。自测里有一道**防回归护栏，不是证明**：它挡住
常见的直接调用写法（用变量、表达式、字符串或点源当命令，`Invoke-Expression`、`Start-Process`、`pwsh`、
`[scriptblock]::Create`，出现产品脚本的文件名），每种都有一段合成代码证明它认得出来。已知它挡不住的：别名和函数
定义（`Set-Alias`、`Set-Item function:`）、.NET 与 CIM 的进程接口（`[Diagnostics.Process]::Start`、
`Invoke-CimMethod`、`[powershell]::new()`）、计划任务，以及写在模块里的定义。

**删目录只有一个入口 `Remove-ParallelInstanceDirectory`**，安装和卸载都用它。它每次删之前都重新检查那个
具体路径：两道路径检查，外加「它本身不是 junction 或符号链接」。本机 pwsh 7.6.6 实测，`Remove-Item -Recurse`
碰到目录里的 junction 只删链接、不顺着删进去；但这是某个 cmdlet 今天的行为，不是这里的代码保证的，所以要删的
路径本身是链接就拒绝，并且自测把实测行为钉住。

另一个删除入口是 `Remove-ParallelInstanceDeploymentConfig`，只删控制端拷来的那个装着密钥的配置文件。它只认布局里的
路径 `<运维目录>\deploy-config.json`，而且必须是普通文件；安装脚本在开工前就拒绝别的路径。
这个文件里是 RIoT 调用密钥、MesIngest 共享密钥和（有的话）V2 恢复凭据的明文，所以从拷上服务器那一刻起，每一条退出路径都要清掉它：安装
脚本从第一个检查之前就包了一层 `try/finally`（定义被拒、还没有布局时，只删安装脚本自己目录下的 `deploy-config.json`）；
安装脚本根本没跑起来时，由控制端事后经 ssh 调用同一个函数再查一遍。删不掉就打印 `SECRET_FILE_LEFT_BEHIND`，不会静默。

除了这两个函数，这几个文件自己不应该删任何东西。自测里同样有一道**防回归护栏，不是证明**：两个脚本和两个模块用
同一个扫描函数、同一套规则，挡住常见的删除写法（删除命令及其别名、带模块名的写法、`cmd`、`robocopy`、`.Delete(`、
`ForEach-Object Delete`、用变量或表达式当命令名），例外按「函数名 + 原文」逐字列出。已知它挡不住的：别名和函数定义、
VB 的 `DeleteDirectory`、FSO 的 `DeleteFolder`、CIM，以及挪走、清空、改权限这类不叫「删」的操作（`Move-Item`
安装脚本自己要用）。

**真正保证不删错东西的是构造，不是扫描**：删前逐次复检的路径白名单与生产清单、拒删链接、服务一步的正面确认与失败
即中止、配置文件只删布局路径（或没有布局时安装脚本旁边那一个）。扫描只是让人改这几个文件时，常见写法不会悄悄绕开它们。

## 文件

| 文件 | 做什么 |
| --- | --- |
| `instance-factory01-v2.json` | 实例定义：端口、目录、服务名、车、RouteGraph、建单闸门 |
| `ParallelInstance.psm1` | 定义的校验、布局（所有路径与名字的唯一来源）、部署足迹、卸载的删除顺序、唯一的删目录函数。检查全是纯函数，例外只有读路径属性的 `Test-ParallelInstanceReparsePoint` 和删目录的 `Remove-ParallelInstanceDirectory` |
| `ParallelHost.psm1` | 读写机器的辅助函数（MVP 服务指纹、调用产品卸载脚本并确认成功、把覆盖层合并进 `appsettings.Production.json` 并回读核对），安装与卸载共用 |
| `Install-ParallelInstanceLocal.ps1` | 在 factory01 上安装／升级／回滚 |
| `Uninstall-ParallelInstanceLocal.ps1` | 在 factory01 上按部署足迹逐项卸载 |
| `Start-FakeMesIngestResident.ps1` | FakeMesIngest 常驻的计划任务入口 |
| `Publish-FakeMesIngest.ps1` | 替身的 self-contained 发布（控制端跑） |
| `Test-ParallelInstance.ps1` | 自测，不碰任何机器 |
| `Invoke-ReverseCheck.ps1` | 在真实定义文件上做的反向验证，不碰任何机器 |

## 改完这里的任何东西之后

```bash
pwsh -File scripts/parallel/Test-ParallelInstance.ps1
```

```bash
pwsh -File scripts/parallel/Invoke-ReverseCheck.ps1
```

两者都要全绿。它们不在 CI 里（本仓 CI 跑的是 .NET 测试套件），所以没人会替你跑。

**孪生脚本**：MVP 那套的对应物是 `8005-workspace` 仓的 `remote-ops/factory-server/scripts/15-deploy-control-server.ps1`
与 `control-server/Install-ControlServerRemote.ps1`。两边刻意分开，所以一边的修复不会自己到达另一边——
改到机器层面的东西（清理通配符、防火墙规则、共享环境变量）时，去看另一边。
