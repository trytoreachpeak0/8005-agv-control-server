# cs#820 第 1 步：并行安全盘点与 L2 实例方案（2026-10-21 00:50，只读）

读的版本：test.yml 取 PR #808 头（`ci/cs803-selfcheck-path-skip`），l2.yml 与 `Invoke-L2Scenario.ps1` 取 PR #805 头
（`perf/cs804-l2-setup-speedup`），其余取 `origin/fp/v2-impl` `e094de3e`。全部用 `git show`/`git grep` 读远端引用，
没建 worktree、没改文件、没碰 runner。issue 已认领（assignee）。

前提：vm-win-01 上两个服务 runner 都是 NETWORK SERVICE，所以它们**共用同一个用户目录**：
`%TEMP%`、`%LOCALAPPDATA%`、`%USERPROFILE%\.nuget\packages` 都是同一份。各实例自己的只有
`C:\actions-runner\<名>\_work`（检出、构建输出、`RUNNER_TEMP`）。下面的判断都以这个为准。

## 一、test.yml（两个 cs-test 实例同时各跑一轮）

| # | 共用物 | 在哪 | 判定 | 说明 |
| --- | --- | --- | --- | --- |
| T1 | 检出、构建输出、`TestResults`、`.trx`、schema-conformance 报告 | `_work\…` | 已按运行隔离 | 每个实例自己的 `_work` |
| T2 | MSBuild 节点、MSBuild server、VBCSCompiler | 机器级 | 已按运行隔离 | job 级已设 `MSBUILDDISABLENODEREUSE=1`、`DOTNET_CLI_USE_MSBUILD_SERVER=0`、`UseSharedCompilation=false`（当初就是为 test 与 l2 同机加的），不会互相挂到对方的节点上 |
| T3 | 单元测试的临时库与临时文件 | `%TEMP%`（两实例共用） | 已按运行隔离 | 约 40 处 `Path.GetTempPath()` 全带 GUID；写死名字的只有 `cs342-*.txt`、`cs563-measure.txt`、`cs597-measure.txt`，都在 `[Fact(Explicit = true)]` 的测量用例里，CI 不跑 |
| T4 | 测试里的端口 | 回环 | 已按运行隔离 | 起真进程的（`ControlServerDatabaseLockTests` 等）用 `FreePort()`，WebHost 用 `:0`；`GateHeldOnboardSendTests` 的 58999 只做参数校验、不绑定 |
| T5 | 出站 schema 编译缓存 | `%TEMP%\controlserver-schema-cache`（共用） | 有锁排队（按设计可并发） | `CompiledSchemaCache` 先写 GUID 临时文件再原子 rename，读坏了当未命中，rename 被占就放弃；本来就有 4 个子进程同时读写 |
| T6 | NuGet 全局包目录与 http 缓存 | `NetworkService\.nuget\packages`（共用） | 有锁排队 | NuGet 自己的跨进程锁，多 restore 并发是它的常规用法 |
| T7 | 命名互斥体与写死端口 | 机器级 | **会撞要改（已改）** | 盘点时（`e094de3e`）test.yml 的脚本都不拿 `Global\W2G-L2PortBlock*` 或 `Global\W2G-InteractiveDesktop`，这一条当时成立。**更正（PR #821 审查 M1）**：cs#809（PR #819，10-20 19:15 UTC 合入）加了 `Test-G3RunnerPorts.ps1`，它在写死的 39011、39013 上起监听，拿写死的 `Global\W2G-Port-39001…39031`（部分不等待），并断言 39019 空闲；两份并发时每轮恰好一份 exit=1。已在 PR #821 用机器级锁 `Global\W2G-SelfCheck-G3RunnerPorts` 包住整个脚本，两份排队约 10 秒，证据在 `evidence/cs820/m1/` |
| T8 | 自检脚本的临时目录 | `%TEMP%` | 已按运行隔离 | `Test-DataRootLockWait`、`Test-InstallerTwoCarPaths`、`Test-G3*`、`Test-L2*`、`Test-NightlyG3` 等都是 `<前缀>-<GUID>`；`Test-ScenarioSqlExplain` 用 `RUNNER_TEMP` |
| T9 | content-conflict 的 stage 根 | `C:\ccsc-<run_id>-<attempt>` | 已按运行隔离 | 同一 run+attempt 只会落到一个实例 |
| T10 | content-conflict 起的 ControlServer 端口 | 回环 | 已按运行隔离（小风险） | `TcpListener(Loopback, 0)` 取空端口再放给进程，有一个毫秒级的“取到后被别人抢”窗口；以前已与 l2、车载端同机并存，第二个 test 实例只是略增概率，不改 |
| T11 | `C:\ProgramData\8005\…` | — | 不涉及 | 测试里只作字符串比较；`Test-ScenarioSqlExplain` 只读校验 MVP 生产库路径没被碰过；`Test-ParallelInstance` 只校验定义文本 |
| T12 | 按进程名杀进程 | — | 不涉及 | tests 与 scripts 里没有 `GetProcessesByName`/`Stop-Process -Name`/`taskkill /IM` |

结论（盘点时）：test.yml 没有“会撞要改”的项，`runs-on: [self-hosted, cs-test]` 不用改。**合入 #819 之后有一项会撞（见 T7 的更正），已在 PR #821 修好。**以后 test.yml 新加的自检，凡是用写死端口或写死锁名的，都要照这条处理。
要改的只有注释（3～12 行和 75 行附近“只有一个 cs-test runner”的说法），这几行不是 cs#803 的自检与跳过映射。

剩下的是资源而不是冲突：
- 内存：一轮 test 约 4～5 GiB。两轮 test＋8 路 L2＋车载端同时在跑，按 2.7 节实测推算整机已提交约 22～25 GiB，
  离 32 GB 上限还有余量，但动态内存跟得慢（2.7 节“可用物理内存最低 0.61 GiB”）。
- CPU：Test 步骤是 BelowNormal，L2 优先。计时敏感用例（多车预算、真实计时器）在三份负载下更容易偶发红，
  第一周的红点要标“负载／产品”。

## 二、l2.yml

| # | 共用物 | 判定 | 说明 |
| --- | --- | --- | --- |
| L1 | 端口槽 1～8（每槽一整块端口） | 有锁排队 | 每个场景运行前拿 `Global\W2G-L2PortBlock-slotN`，Authenticated Users 可开，跨账户可排队（搭建记录 1.4 已实测）。两个实例都用 1～4 时**不会静默撞端口**，而是 `L2_PORT_LOCK_WAITING` 逐个场景交替排队，最长等 3600 s 后报 `L2_PORT_LOCK_BUSY`；端口 owner 不对时还有 `Assert-L2PortOwner` 兜底。吞吐不变，所以两实例方案必须分槽段 |
| L2 | 槽 0 | 不涉及 | 只有真装置、run-journey-g3 用；合成 lane 永不用 0 |
| L3 | 构建输出、证据根 | 已按运行隔离 | 都在本实例 `_work`／`RUNNER_TEMP`，按 run_id+attempt 命名 |
| L4 | 场景 stage 根（SQLite 库等） | 已按运行隔离 | 步骤里把 `TEMP/TMP` 改到证据根下 `_stage`，名字再带时间戳与槽号 |
| L5 | `%LOCALAPPDATA%\8005-l2-peers`、`%LOCALAPPDATA%\8005-l2\rig-runs.log` | 不涉及合成 L2 | 只有 Onboard='Real' 的场景用（真装置作业，单个 cs-desktop runner，账户也不同）；运行台账本身按 `FileShare.Read` 追加、可并发 |
| L6 | 替身默认端口（58008、58088、58009、1502/58006） | 已按运行隔离 | `Invoke-L2Scenario.ps1` 按槽把整块端口传给每个组件，脚本与场景里没有绕过槽的端口字面量 |
| L7 | NuGet、MSBuild 节点 | 同 T2、T6 | job 级已关节点复用与 build server |
| L8 | 真装置提交量门槛 | 资源，不是冲突 | 见第四节 |

结论：**1 个实例时没有要改的；2 个实例时唯一必改的是 L1，按实例分槽段**（`L2Lanes.psm1` 的 lane→slot 现在写死 `Slot = Lane`）。

另外两处要知道：
- main 与 `ControlServer_MVP` 上的 test.yml、l2.yml 仍是 `[self-hosted, headless]`，所以现有 `headless` 实例要保留这个标签；
  新的 `cs-l2` 只给 fp/v2-impl 的 l2.yml 用。
- g3.yml 的 `register`、`gate`、`report`、release.yml、夜间 trigger 都在 `headless`。L2 改到 `cs-l2` 后，`headless`
  上就只剩这些短作业和 main/MVP 推送，trigger 自然不再排在 L2 后面，不必另起标签。trigger 文件在 main，cs#807 的 PR #818
  也改它，要按它的合入顺序协调。`Test-NightlyG3.ps1` 只断言 g3 作业在 `cs-desktop`，不查 `headless`，改 gate/report 不会让它红。

## 三、L2：1 实例 × 8 路，还是 2 实例 × 各 4 路分槽段

已有实测（新机，搭建记录 2.6）：8 路一轮场景步骤 886 s、汇总 845 s、作业 932 s、全过、整机已提交最高 13.4 GiB；
4 路一轮汇总 1656 s、作业 1673 s。

| | 1 × 8 路 | 2 × 4 路（槽 1～4／5～8） |
| --- | --- | --- |
| 单轮出结果 | 约 15.5 分钟 | 约 28 分钟 |
| 吞吐 | 约 3.9 轮／小时 | 约 4.3 轮／小时（两轮同时，各 28 分钟） |
| 5 个 PR 同时转 ready，最后一个出结果 | 约 78 分钟 | 约 84 分钟（3 波） |
| 只有 1 个 PR 时 | 8 路全用上 | 4 个槽空着 |
| 要改的代码 | 只改 `L2_LANES` | 槽段分配（按 `RUNNER_NAME` 或 runner 环境变量）＋并发红证据 |
| 内存 | 一次构建 | 两次构建与两份作业开销同时在跑，峰值略高 |
| 同时在跑的场景数 | 8 | 8（一样） |
| 一个 runner 卡死 | L2 全停 | 另一个还能接活 |

**推荐 1 × 8 路。** 理由：
1. 同时在跑的场景都是 8 个，CPU 与时序干扰一样，但 1 × 8 单轮快将近一倍，扎堆时吞吐只差约 10%，不扎堆时 2 × 4 还浪费一半槽。
2. 只改一个数，不引入“两个实例拿到同一段”的新故障面，也不用为它造并发红证据。
3. 8 路已在新机实测全过。
4. 2 × 4 唯一实在的好处是 runner 冗余；这条可以用别的办法拿：留一个不挂 `cs-l2` 的备用实例，卡死时手工挂上。

第 2 步仍按票面实测：在合入 #805 后的提交上用 8 路跑对照，并用 workflow_dispatch 同时派 3～4 轮模拟扎堆。
2 × 4 不实现，按上面的已有数据写理由；如果调度要实测两种方案，就得先写分槽段代码，多 2～3 轮 CI。

## 四、真装置提交量门槛与新的并发组合

`RIG_COMMIT_START_CEILING_GIB` 现在已经是 job 环境变量，改成 20 只需改值和注释。注意：两轮 test＋8 路 L2 同时在跑时，
整机已提交大约就到 18～20 GiB，真装置会在门槛前等（最长 30 分钟，到点报 `RIG_COMMIT_GUARD`，不算产品红）。
所以“门槛 20”与“第二个 cs-test 实例”要一起测，验收那轮 `real-onboard-normal-load` 最好在 test 与 L2 都在跑时派，看日志里的
`RIG_COMMIT_WAITING` 与开跑时已提交量。

## 五、第 2 步动手前要核对（只读）

- vm-win-01 上的实际情况：runner 作业里的 `TEMP` 是否就是 NetworkService 的 Temp（结论不变，只是记录）；C 盘剩余空间（每个服务端
  `_work` 约 2 GB）；搭建记录“新机必做项”1～10。
- 新实例名建议：`vmwin01-control-server-3`（第二个 `cs-test`）、`vmwin01-control-server-l2`（`cs-l2`）；先挂临时标签验证。
- PR #805、#808、#818 的合入状态。
