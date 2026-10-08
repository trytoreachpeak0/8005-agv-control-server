# cs#512 证据：v2 并行实例的 FakeMesIngest 计划任务以 SYSTEM 启动即 -1

票：[control-server#512](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/512)。
出处：cs#411 第二步 10-07 在 factory01 首装 v2 并行实例，安装在替身健康检查处中止（`evidence/field/2026-10-03-B9-charging-roster-and-policy/import-1007/03-deploy.txt`）。

## 一、结论

- **修法**：计划任务不再以 `pwsh -File Start-FakeMesIngestResident.ps1` 为动作，改为直接执行
  `ControlServer.FakeMesIngest.exe --FakeMesIngest:listenAddress=127.0.0.1 --FakeMesIngest:port=<端口>`；
  种子改由安装器在替身应答 health 后、于自己的 ssh 会话里灌一次（`Invoke-ParallelFakeMesIngestSeed`）。
  这一形态 10-08 在 factory01 上用临时任务实测通过（R4，第 8 号文件）。
- **根因（定位到这一层为止）**：在 factory01 上，SYSTEM 计划任务里的 pwsh 去跑
  `C:\Program Files\8005 AGV\ControlServer.V2.FakeMesIngest\Start-FakeMesIngestResident.ps1`、带
  `-ExecutablePath "<替身 exe>"` 那组参数时，进程在 PowerShell 主机初始化之前就没了（`LastTaskResult`
  0xFFFFFFFF，没有 PowerShellCore/Operational 40961）。触发器、重启策略、裸名 `pwsh.exe` 都**不是**原因
  （R2、R3）；同样的 pwsh、同样 `-ExecutionPolicy Bypass -File` 去跑 D:\ 下的小脚本是正常的（Y-T2）。
  再往下——脚本位置、命令行里的 exe 路径还是工作目录——没有分。机器上运行着火绒（`HipsDaemon`），
  按命令行拦截的可能最大，**这是推断，没有直接证据**。
- **绝对路径保留，但它不是这次的修复**（R2 证明只换绝对路径仍然 -1）。
- **同形态在 vm01（Win11）上能起来**（第 1 号文件），所以 vm01 上的绿证明不了 factory01。

## 二、factory01 上做了什么（每一项都先经调度审原文；Y、R1/R2、R2 补做、R3/R4 各由用户在调度会话里单独授权一次）

每次前后都读了 MVP 服务 `8005 AGV ControlServer` 的 pid 与 58005/58007 的监听进程，**全部一致**
（pid 35288）。V2 服务没有被停、启或修改，`JourneyRuntime.enabled=False`。

| 文件 | 时间 | 内容 | 结果 |
| --- | --- | --- | --- |
| 02 | 10-08 16:27 | 只读：任务 XML、任务信息、Operational 日志状态、Application 崩溃、pwsh 位置 | 任务定义与安装器预期一致；TaskScheduler/Operational **未开**；无 pwsh 崩溃；machine PATH 有 pwsh 7.6.5 |
| 03 | 10-08 | 只读第二轮：pwsh 安装时间、安全软件、环境变量名、SYSTEM 配置目录、事件日志 | pwsh 09-01 12:49 MSI 安装，早于 09-01 23:35 开机（排除"计划任务服务 PATH 旧"）；**火绒 `HipsDaemon` 在跑**；10-07 19:12:01～19:14:45 之间**没有任何 40961**，而前后每个 ssh 会话里的 pwsh 都有 |
| 04 | 10-08 17:37 | Y：两个临时 SYSTEM 任务，绝对路径 pwsh；T1 `-Command "exit 7"`，T2 `-ExecutionPolicy Bypass -File` D:\ 小脚本 | T1=7、T2=9，均有 SYSTEM 40961，T2 写出文件；执行策略 LocalMachine=RemoteSigned，其余 Undefined |
| 05 | 10-08 17:5x | R1：10-07 那个任务原样启动一次 | **脚本写法错误把结果吞掉**（函数输出混进返回值），R2 被跳过；清理正常 |
| 06 | 10-08 18:13 | R1 只读补读 | 0xFFFFFFFF、日志目录不存在、无 40961 → R1 失败 |
| 07 | 10-08 18:27 | R2：只把 Execute 换成绝对路径 | 0xFFFFFFFF、无 40961 → 裸名不是原因；Execute 已改回 |
| 08 | 10-08 19:13～19:16 | R3：同 R2 的动作，配 Y 的设置（无触发器、无重启）；R4：现行形态，由 702a25d7e 的共用函数生成，安装器全套设置，端口 47188，再灌一次种子 | **R3 失败**（0xFFFFFFFF、无 40961）；**R4 成功**：约 3 秒回 health，唯一进程属 `NT AUTHORITY\SYSTEM`，任务 Running，灌种子 reset→读回 0 条→revision 1，灌后替身仍在 |

两个旁证：

- **`LastRunTime` 在 factory01 上不可信**：Y 的两个任务真实启动于 17:37:04、17:37:07（40961 与 probe 文件为准），
  `LastRunTime` 都显示 17:37:37；10-07 那次显示得比注册时间还早 31 秒。三台机器的时钟彼此只差几秒，
  不是时钟问题。诊断里 `LastRunTime` 只打不用。
- `03` 的原文 680 KB，几乎全是脚本块日志（4104），内容是本仓自己的脚本，入库时去掉，只留每段结论、
  事件计数与 40961/53504 时间线。机器级环境变量只留名字（值只取 PSModulePath、POWERSHELL_* 这几类）。

## 三、vm01

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| 01 | 修前：任务构造照抄 35b8ae81 安装器第 414-428 行，SYSTEM，路径带空格，端口 47188 | **不复现**：2 秒内 health 200，包装日志写到 Serving；清理后任务、目录、端口、进程均无残留 |
| 09 | 修后：`Test-FakeMesIngestScheduledTask.ps1`（702a25d7e） | 待补 |

## 四、本机自测（不在 CI 里，`scripts/parallel/README.md`）

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| 10 | `Test-ParallelInstance.ps1`（702a25d7e） | 480 passed, 0 failed；本票 8 条：动作拒绝 3 条、动作恰为 exe+两参数、灌种子对无人端口抛出、运维入口非零退出、安装器只经共用函数建任务／等待／灌种子 |
| 11 | `Invoke-ReverseCheck.ps1` | 22 passed, 0 failed |
| 12 | 变异 A1～A4 | 各自恰好打红对应用例，其余不变；还原后工作树干净 |

另：本机以自己的账户（不建任务）按同一动作起真替身，`Invoke-ParallelFakeMesIngestSeed` 返回一行
`Catalog now holds 0 demand(s) at revision 1`，灌后替身仍在，按 pid 停掉。

中间形态（1d26c8093，任务仍跑 pwsh 包装脚本、只改绝对路径与诊断）也做过三处变异（全杀），
那部分代码已被 702a25d7e 取代，记录不入库。

## 五、本票自己犯的两个错

1. R1 探针里 `$r1 = Invoke-Round 'R1'` 把函数里所有结果行连同返回值一起收进 `$r1`，恒为真，结果没打、R2 被跳过。
   用只读补读救回了 R1 的结论，R2 另行授权补做；之后的探针结果一律 `Write-Host`、不用函数返回值。
   产品代码里同类隐患（`Register-ParallelFakeMesIngestTask` 里的 `Start-ScheduledTask`）一并改为 `$null =`，
   vm01 自测断言注册只返回一个时间戳。
2. vm01 第一次修后复现，`Out-File` 用了相对路径而当时 cwd 在别的仓的临时 worktree，本地管道开头失败；
   远端 ssh 已起并跑完、自己清理了现场，输出全丢。经调度批准重跑（第 9 号文件）。
