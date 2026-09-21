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
