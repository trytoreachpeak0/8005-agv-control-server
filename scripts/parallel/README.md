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
这个文件里是 RIoT 调用密钥和 MesIngest 共享密钥的明文，所以从拷上服务器那一刻起，每一条退出路径都要清掉它：安装
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
| `ParallelHost.psm1` | 读机器的辅助函数（MVP 服务指纹、调用产品卸载脚本并确认成功），安装与卸载共用 |
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
