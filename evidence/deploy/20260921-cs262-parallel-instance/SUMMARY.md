# control-server#262 并行实例部署机制——本机可验证部分的证据

日期：2026-09-21
分支：`b7-262/factory01-parallel-deploy`
范围：**本票不含任何一次真实安装**。下面每一项都是在控制端本机跑的，或者是对
`factory01` 的**只读**探测。第一次真正装到 `factory01` 要单独向用户申请。

## 一句话结论

部署机制做完并验证过：配置校验的正反例全绿，反向验证是在**真实文件**上做的；
FakeMesIngest 的常驻方案在本机实跑通过，包括崩溃后自动恢复；端口与目录按
`factory01` 的实测现状定死，没有一项留到"到时候再定"。

## 一、配置校验的自测

`test-parallel-instance.txt`，命令：

```
pwsh -File scripts/parallel/Test-ParallelInstance.ps1
```

**37 项全绿。** 结构是：1 条正例（出厂的实例定义必须被接受）、26 条反例、2 条开关
用例、5 条配置叠加用例。

反例的判据比"它红了"强一档：**每条用例声明它期望哪一条失败，断言是"期望的那条出现
了，而且失败总数恰好等于声明的条数"**。多出来的失败必须写进用例的期望列表里，写的
人得解释为什么这个注入会碰到那一条。只有一条用例是两条失败（把 `mesIngest.baseUrl`
指回生产的 5088，同时就让它和 `fakeMesIngest.port` 对不上了），两条都在期望列表里。

每条用例还先比对注入前后的定义指纹，**注入没落地的用例判失败**，而不是让它看起来像
"判据不够强"。

## 二、反向验证（验收项 3 与 4）

`reverse-check.txt`，命令：

```
pwsh -File scripts/parallel/Invoke-ReverseCheck.ps1
```

**14 项全绿。** 这一份和上一份的区别是：它改的是**磁盘上那个 `instance-factory01-v2.json`
文件**，不是内存里的副本。票面要的是"把配置拿掉，它必须拒绝"，而部署脚本读的是文件。

每个用例的输出里有三样东西：

1. `git hash-object` 算出的 blob 哈希改前改后，**证明这次编辑真的落到了文件上**；
2. 拒绝时列出的全部理由，**所以"是哪一项让它红的"是读出来的，不是推出来的**；
3. 从改动前拍的字节备份还原，再核对哈希回到基准。用备份而不是 `git checkout --`：
   后者会把同一文件里未提交的真实改动一起抹掉。

覆盖的注入，按票面点名的顺序：

| 用例 | 注入 | 拒绝理由 |
| --- | --- | --- |
| case 0 | 只重排格式、不注入 | **接受**（否则下面每一条都可能是被格式化搞红的） |
| case 1 | 整个 `routeGraph` 节删掉 | `routeGraph must be an object` |
| case 2 | `routeGraph` 写成空对象 `{}` | `routeGraph.enabled must be stated explicitly` |
| case 3 | `enabled` 写成字符串 `"false"` | `must be a JSON boolean` |
| case 4 | `agvId` 改成 agv01 的名字 | `driving in production` |
| case 5 | `vehicleKey` 改成 agv01 的 key，`agvId` 仍是 agv02 | `driving in production` |
| case 6 | `agvId` 写成 `AGV02` | `does not match the RIoT deviceName of agv02` |
| case 7 | `agvId` 写成数组，里面混进 agv01 | `must be a single string` |
| case 8 | `agvId` 写 agv03、`vehicleKey` 仍是 agv02 | `The name and the key must describe the same car` |
| case 9 | `mesIngest.baseUrl` 指回生产 MesIngest | `the production MesIngest` |
| case 10 | 服务名与安装根都用 MVP 的 | `must not install over the MVP service` |
| case 11 | `onboardPort` 取 58005 | `the MVP onboard NDJSON transport` |
| case 12 | `listenAddress` 写 `0.0.0.0` | `AGV-Internal switch where the CI runner lives` |

case 5 到 case 8 是这套检查真正比"单字段白名单"强的地方：**车号按 `agvId` 与
`vehicleKey` 成对匹配 RIoT 登记表判定**。只查 `agvId` 会放过一个被改过名的车（RIoT 不
保证 `deviceName` 唯一），只查 `vehicleKey` 会放过一份人读起来指向另一台车的配置，而
两者都查不到的那个状态——改了一半——正是 case 5 和 case 8。

### 这一轮抓出了我自己的一个真 bug

`Invoke-ReverseCheck.ps1` 的 case 0 第一次跑就红了，报的是
`The property 'Count' cannot be found on this object`。

根因：`Assert-ParallelInstanceDefinition` 里 `[string[]] $failures = Test-...` 少包了
一层 `@()`。PowerShell 的函数返回空数组时会把它 unwrap 成"什么都没返回"，
`[string[]] $null` 仍然是 `$null`，StrictMode 下对它取 `.Count` 就抛。

**后果是：一份完全合法的定义会被"拒绝"**，而拒绝理由是个 PowerShell 内部错误。部署
脚本用的正是 `Assert-`，所以这个 bug 会让并行实例一次都装不上。

为什么自测没抓到：自测当时只调 `Test-`，而每个调用点自己都包了 `@()`。**工具没说不
等于事实没有**——只测了包装层里面那个函数，包装层自己的失败模式就没人看。已修，并补
了两条针对 `Assert-` 的用例（合法定义必须正常返回；agv01 定义必须抛），互为对照，免得
"永远不抛"也能让第一条绿。

## 三、FakeMesIngest 常驻方案的实跑

`fake-mes-ingest-residency.txt`，脚本 `run-resident-evidence.ps1`，种子
`seed-test.json`。在控制端本机跑，loopback 58188。

| 步骤 | 结果 |
| --- | --- |
| 1. 首次启动 | 替身起来、健康返回 200、按种子灌进 2 条需求，`catalogRevision=3` |
| 2. 杀掉替身进程（模拟崩溃） | **包装脚本随之退出**（日志记下 `exited with code -1`），端口释放 |
| 3. 再次启动（计划任务重启策略会做的事） | 目录**又回到那 2 条需求**，替身是新 pid |
| 4. 用 `--FakeMesIngest:listenAddress=0.0.0.0` 起它 | **退出码 2，拒绝启动**，stderr 说明原因 |

第 2 步是这个方案成不成立的关键：计划任务的重启策略作用在**它启动的那个进程**上，也
就是包装脚本。如果替身死了而包装脚本还活着，任务在 Windows 看来仍然"正在运行"，永远
不会重启——机器上就只剩一个空壳。实测确认包装脚本跟着退出，且退出码非 0。

第 3 步是对"内存态目录重启即空"这个缺陷的解法的验证：**种子文件是权威状态**，每次启动
先 `reset` 再照种子灌一遍。

### 实跑中改掉的一处

第一版脚本自己编了个 `runId` 去发命令，被替身以 **409 `RUN_ID_MISMATCH`** 拒绝，两条
需求一条没进，而脚本照样进入"Serving"状态——**日志里有 `SEED FAILED`，但服务看起来是
好的**。`CommandEngine.Apply` 要求命令的 `runId` 等于替身当前轮次的 id，那个 id 是替身
自己生成的。改成先 `POST /control/v1/reset`、用它返回的新 `runId` 发后续命令。同时加了
一步"把目录读回来数一遍"，因为 **"两个 PUT 返回 200" 和 "目录里有两条需求" 是两件事**。

## 四、`factory01` 只读探测

`factory01-survey.txt`，2026-09-21 08:56 取。**只读**：没有碰 MVP 的进程、安装目录、
配置或数据库，没有碰生产 MesIngest，没有碰 `SQAGV`。

支撑本票决定的几项：

- **临时端口 261 / 16384 = 1.6%**，健康。这是 2026-09-08 被一个泄漏连接的服务占到 64%、
  让 CI 停摆 4 小时 14 分的那个池子。本票把它记成基线，给后面"量路网引擎开销"那张票用。
- **本票要的四个端口 58105 / 58107 / 58109 / 58188 全部空闲**。MVP 占着 58005 / 58007，
  生产 MesIngest 占 `0.0.0.0:5088`。
- **`curl.exe` 确认不存在**（Server 2016）。本票新写的脚本一处都没用它。
- **没有任何 `8005` 或 `ControlServer` 相关的计划任务**，所以 FakeMesIngest 的任务名不会
  撞上谁；防火墙里只有 MVP 那两条 `8005 AGV ControlServer 5800x`。
- **内存是这台机器上最紧的资源**：物理 31.5 GiB、空闲 5.6 GiB；已提交 29.6 / 36.3 GiB，
  余量 6.7 GiB。MVP 的 `ControlServer.Host` 自己吃 876 MB 工作集。第二套按同量级估，加上
  FakeMesIngest，预计再吃 1～1.5 GiB。**装得下，但余量会变薄**，这条要写进第一次真装的
  申请材料。
- 机器级的 `CONTROL_SERVER_RIOT_CALL_API_KEY`（166 字符）与 `CONTROL_SERVER_ONBOARD_CREDENTIAL`
  （64 字符）都在，是 MVP 装的、两套共用；`CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD`
  不存在。并行实例**只读不写**这三个。

## 五、没有验证的部分，以及缺什么条件

| 没验的 | 为什么 | 需要什么 |
| --- | --- | --- |
| 在 `factory01` 上真装一次 | 不在本票范围，要单独授权 | 用户批准那一次具体的安装 |
| 产品脚本在第二套参数下真的跑通 | 同上。参数名已逐个比对确认存在（见下） | 同上 |
| 两个实例同时运行时的内存与端口实况 | 要装完才有 | 同上 |
| 路网引擎开着时的资源占用 | 是下一张票的事，本票只保证有地方量 | 这套环境装好 |
| `mapId` 到底是 25 还是 26 | 仓里 `appsettings.json` 写 25、`MAP-25-*` 一整套标识符都绑在 25 上；工作区 `CLAUDE.md` 说用户 2026-09-19 更正为 26 | 见主文档"一个必须先澄清的矛盾" |

产品脚本参数名是用 AST 读 `param` 块逐个比对的，不是凭印象写的：
`Install-ControlServerLocal.ps1` 的 `ServiceName`、`InstallRoot`、`DataRoot`、
`BackupRoot`、`ListenAddress`、`HealthBindAddress`、`OnboardPort`、`HealthPort`、
`SkipMachineEnvironmentInjection` 全部存在；`Update-ControlServerLocal.ps1` 的
`ServiceName`、`InstallRoot`、`DataRoot`、`BackupRoot`、`CertificatePasswordVariable`、
`VerifySafetyProjectionReadOnly` 全部存在。**所以产品脚本一行未改。**

## 六、没跑全量测试套件

本票没有改 `src/` 或 `tests/` 里的任何文件，只新增了 `scripts/parallel/`。按本仓
`CLAUDE.md` 的规矩（"Run tests only when the current task changed product code, tests,
or build inputs"），没有跑那条命令。CI 那一轮在转 ready 时跑。

---

## 七、审查之后（2026-09-21 上午）

调度转来的审查：零严重、八条中等、四条疑问。下面是逐条的处理与证据。

### 必改四条

**中1：`*.incoming-*` 清理会删掉另一套部署正在用的暂存目录。** 两套的包目录都在
`D:\zhengyushao` 下，旧写法在父目录里删所有 `*.incoming-*`，不带实例前缀，所以两边互相都在对方
的命中范围内。这个 bug 是从 MVP 的 `Install-ControlServerRemote.ps1` 抄过来的，复制那一刻就在。
两边一起改成 `"<包目录名>.incoming-*"`，四个孪生脚本头注释互相点名。`test-parallel-instance.txt`
的「Staging cleanup glob」一节在临时目录里重现：**旧通配符两个都命中**（夹具确实重现了 bug），
新通配符从两边各自只命中自己的。`Assert-MvpUntouched` 抓不到这类问题——它只比服务、不看磁盘，
这一点写进了 `ParallelHost.psm1` 的注释。

**中3：车号白名单漏了 `Fleet` 名册——修的时候又找到第二个洞。** `ConvertFrom-Json -AsHashtable`
**区分大小写**（本机实测：`{"agvId":…,"AgvId":…}` 两个键都保留，`ContainsKey('AGVID')` 为假），
而 .NET 配置不区分。所以只拒绝 `fleet` 挡不住 `Fleet`，同时写 `agvId`／`AgvId` 两个键也能绕过
按名字读的检查。改成**每一节只认白名单里的键、精确到大小写**；`Fleet` 是刻意排除的那一个。

红证据 `review-red-old-module-accepts-fleet.txt`：用修改前的模块（`HEAD df7b4dae`）跑三种注入，
**失败数都是 0，全部放行**。绿证据：自测里对应三条各只红一条；`reverse-check.txt` 的 case 13、
case 14 在真实文件上各只红一条。自测另加一条守卫：白名单里的每个键都要能在对应 C# 选项类里找到
同名属性（改名会红），并断言 `Fleet` 在 C# 里存在、在白名单里不存在。

**中5：装完就是 `-Intake` 状态。** 这是文档问题，已写进主文档第 11 节的第一小节，放在申请材料
表格之前：接真实数据、做真实决策、不动车；与 MVP「装」和「开运行时」两次决定不同；想装完全关要
同时把 `journeyRuntime.enabled` 与 `routeGraph.enabled` 改成 `false`。

**中6：卸载没有完整的路。** 新增 `Uninstall-ParallelInstanceLocal.ps1`，逐项读
`Get-ParallelInstanceFootprint`——**安装脚本的名字（防火墙规则名、证书变量名）也从同一处取**，
所以不会有「装了、卸载不知道」的东西。默认保留数据根、备份与运维目录，`-RemoveData` 才删；用户级
RIoT key 永远不删（MVP 每次安装都读它）。`uninstall-whatif.txt` 是本机干跑列出的清单，什么都没删。
走查时发现并修了一处：首次安装半路失败时 `packageRoot` 不存在，原先只去那里找产品卸载脚本，
服务那一项会删不掉——而这正是卸载最该派上用场的场景。现在按包目录、上一代、运维目录三处依次找，
控制端每次都把产品卸载脚本随编排脚本一起送到运维目录。

### 建议改与记录

- **中2（dashboard）**：文档与 README 明写「不装、也不支持装」，说明三个 dashboard 参数默认值都是
  MVP 的身份、端口没有兜底。
- **中8**：`Publish-FakeMesIngest.ps1` 不再写死 SDK 版本号；文档第 13 节改成照实描述会动哪个机器级
  变量；补了目录存在性探测 `factory01-survey-paths.txt`（八个目录、服务名、任务名、两条防火墙
  规则全部不存在，四个端口空闲且没有被连接当作本地端口占用）；「升级时定义里的端口不生效」写进
  文档，`-WhatIf` 也会提示。
- **中4**：文档第 12 节加了「V2 起不来、报 bind 失败」→ 先查端口是否被临时连接占着。
- **中7**：安装日志改成照实说「写进用户级、与 MVP 共用」。

### 四条疑问

1. **mapId**：用户已答（MVP 25、v2 26）。实例定义暂时保持 25 作占位，主文档第 9 节与本目录的
   `scripts/parallel/README.md` 写明「v2 目标 map 26，`dispatchZone` 与准入策略部署号在真装第一步
   从 factory01 直查 RIoT 取回」。**按现在的定义原样装会指向 MVP 那张图，校验拦不住**，这一条写在
   申请材料里。转来的出处都实读过：真机证据确认 map 26 叫 `老厂前线new_wk`；`tests/` 里的
   `MAP-26-*` 确是测试编的；`src/` 没有硬编码的 25。
2. **替身重启后服务端的反应**：写进文档第 12 节的盯守清单，并**修正了一处前提**。实跑证据里两次
   启动的 ETag 都是 `catalog-h8f14e45f…-r3`：`historyEpoch` 是种子里的常量，不会变；种子不变时
   revision 也回到同一个值，这一种没有问题。真正的风险更窄：**改了种子文件再重启**会得到同一个
   epoch、同一个 revision、不同的内容，而 ETag 只由这两者推导；另一种是运行中临时加过需求后重启、
   revision 回落。
3. **计划任务那一层没实跑过**：写进盯守清单，附真装后的验证办法。
4. **首次安装半路失败的退路**：安装脚本失败时现在会明确打出「可能半装」并列出 MVP 指纹前后值；
   文档第 11 节写了三步退路。

### 这一轮自己又撞上的两次

- 给控制端脚本打补丁时，PowerShell here-string 吃掉了末尾换行，`Write-Step '…'` 和下一行的 `return`
  粘成了一行。**这是合法语法**：`return` 变成 `Write-Step` 的第二个字符串参数，`-WhatIf` 干跑之后
  **不会停，会继续往下真去部署**。语法检查抓不到。修掉之后写了一个按 AST 找「命令收到关键字作为
  裸参数」的扫描，先用一对故意粘连／干净的样本校准（报 1／报 0），再扫本票全部 11 个脚本：只剩
  两处 `git hash-object`／`git rev-parse` 被 Verb-Noun 规则误认的误报。
- 测试里一个脚本块返回单元素数组被展开成标量，`.Count` 在 StrictMode 下失败——和第二节那个
  `Assert-` 的 bug 是同一个机理，第二次撞上。自测当场报错，没有漏过去。

### 数字

| | 审查前 | 审查后 |
| --- | --- | --- |
| `Test-ParallelInstance.ps1` | 37 | **54**，全绿 |
| `Invoke-ReverseCheck.ps1` | 14 | **16**，全绿 |

## 八、聚焦复审之后（2026-09-21 中午）

复审给了一条严重（S1）和四条中等（M1–M4），五条都改了。本节的输出都是在合并了
`fp/v2-impl` 顶端 `e9df58cd` 之后的树上重跑的（合并提交 `048b5aea`），和推上去的最终树一致。

### S1：一种很常见的写错，会让卸载删掉 MVP

复审的复现：实例定义里把路径写成正斜杠（`C:/Program Files/8005 AGV/ControlServer`），或者
`packageRoot` 写成 `D:\zhengyushao\ControlServer.previous`，**校验通过，卸载的删除清单里出现 MVP 的
安装根、生产数据根、MVP 回滚要用的上一代包和生产 MesIngest 目录**。三个根因叠在一起：

1. 路径只做了字符串比较，正斜杠、`.`／`..`、短名、UNC 全被判成「不是生产路径」；
2. 生产路径清单不全，漏掉的五个目录就在这张票自己的探测证据 `factory01-survey-paths.txt` 里；
3. 产品卸载脚本本来能认出正斜杠写法并以 production 拒绝，但外层卸载脚本把这个拒绝当普通失败吞掉、
   继续往下删目录——**内层护栏认出了危险，外层把它当成可以跳过的错误**。

修法对着根因各一条：路径只认一种规范写法，且必须直接位于三个已知根目录之一下、名字带 `V2` 标记
（白名单作第一道）；生产清单补全、规范化后再比、读不懂的写法按生产处理（第二道）；删除顺序收进
`Invoke-ParallelRemovalSequence`，服务那一步一失败就整体中止，每删一个目录前重新过两道检查。

复审给的那份复现定义，现在在校验这一步就被拒，卸载脚本一条计划都不打（自测里有这一条）。

### M1–M4

| 条 | 问题 | 处理 |
| --- | --- | --- |
| M1 | 路径和名字在安装、足迹、卸载三处各写一遍，会漂 | 全部由 `Get-ParallelInstanceLayout` 产出；服务名／任务名拒绝通配符字符。自测扫描安装脚本里绕开布局直接读路径的写法，并先埋两处证明扫描不瞎 |
| M2 | `-Uninstall -WhatIf` 会先往服务器拷文件，而且会把安装时的定义覆盖成控制端现在这份 | 安装在动手前记下 `installed-instance.json`，卸载默认按它；控制端 `-Uninstall` 只做一次只读 `Test-Path`，不拷任何东西 |
| M3 | 定义写着 map 25，「按原样装会指向 MVP 的图」只写在文档里 | 用户答复 MVP 用 25、v2 用 26。定义改为 26；`dispatchZone` 与准入策略部署号没有出处，写成 `REPLACE_*` 占位。校验拒绝占位，也拒绝 25、`老厂前线new`、`MAP-25-*` |
| M4 | 反向验证中途抛异常时，真实定义文件会留在注入过的状态 | 整段包进 `try/finally`，从字节备份还原；收尾判据改为和运行前的 git 状态比 |

### 证据文件

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `review2-test-parallel-instance.txt` | 自测 | **105 通过，0 失败** |
| `review2-reverse-check.txt` | 在真实定义文件上的反向验证 | **21 通过，0 失败**；收尾时文件 blob `971ee4d7…` 与运行前一致，git 状态与运行前一致 |
| `review2-mutation.txt`（脚本 `review2-mutate-guards.ps1`） | 在一次性副本里逐一拆掉护栏，看哪些用例红 | 六个变异各自只红对应用例，见下表；真实文件未被改动，副本已删 |
| `review2-uninstall-whatif.txt`（脚本 `review2-uninstall-whatif.ps1`） | 在本机跑服务器端卸载脚本的 `-WhatIf` | 出厂定义：被三处占位拒绝，不打计划。填好占位的副本：列出完整计划，服务排第一，停在 `WhatIf` |

以前那份 `uninstall-whatif.txt` 是修改前的卸载脚本生成的（服务不是第一步、没有残留暂存目录那两项），
留着作对照，不再代表现状。

变异结果：

| 拆掉的护栏 | 红的用例数 | 说明 |
| --- | --- | --- |
| 服务那一步失败不再中止 | 1 | 「production 拒绝之后没有别的动作」 |
| 删除序列里的两道目录检查 | 2 | 通配符匹配到白名单之外、足迹里混进生产目录 |
| 正斜杠与 `GetFullPath` 回读检查 | 3 | 含复审的复现定义 |
| 白名单的 `V2` 标记检查 | 6 | 含「通配符匹配到 MVP 暂存目录」——**只靠生产清单会放行它**，因为 `ControlServer.incoming-…` 和 `ControlServer` 名字不同也不嵌套。白名单在承重 |
| 通配符字符检查 | 3 | |
| map 25 拒绝 | 2 | |

### 这一轮自己撞上的三次

- **`Join-Path` 在没有 `D:` 盘的机器上报错**：控制端没有 `D:`，布局函数一调用就抛 `Cannot find drive`。
  模块里改成字符串拼接。
- **反向验证脚本里一处双反引号让续行失效**，脚本中途终止，真实定义文件被留在「填过值」的状态。
  按 blob 哈希从备份还原回 `971ee4d7`，然后加了上面 M4 那个 `try/finally`，并故意在中途抛一次异常，
  确认 finally 真的还原了。M4 是复审指出的，但我在改它之前先自己踩中了一次。
- **收尾判据误把运行前就有的 ` M` 状态当成本次运行留下的改动**，改为和运行前的状态比。

另外，给证据生成 `-WhatIf` 输出时我的脚本漏传了 `-ConfirmUninstall`，两份都报了同一句
`Explicit -ConfirmUninstall is required.`。回头核了控制端 `19-deploy` 的两处调用，都带着这个开关，
所以只是证据脚本的错，补上重跑。

### 数字

| | 第一轮审查后 | 聚焦复审后 |
| --- | --- | --- |
| `Test-ParallelInstance.ps1` | 54 | **105**，全绿 |
| `Invoke-ReverseCheck.ps1` | 16 | **21**，全绿 |
