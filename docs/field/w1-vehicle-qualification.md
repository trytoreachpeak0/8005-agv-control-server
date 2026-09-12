# W1 现场窗口操作手册：三车逐仓 IO 核对与门禁逐台启用

本窗口要做的事只有两件：**三台车逐台逐仓核对**，**每台核对通过后放行它自己的
`SlotConfigurationReadiness`**。做完之后启用门禁。

**顺序不得倒置：先核对，后启用门禁。**反过来会让三台现有车同时失去业务就绪，而那种排法没有任何
需求依据——`REQ-0259` 只规定「未达 readiness 不得业务就绪」，没规定门禁必须先于核对上线。脚本按这个
顺序编排，第一台车放行之后才记门禁启用。

**空载受控急停演练不属于本窗口**，它是批次 2 轨 B 的现场前置，另行安排。

## 一、开工前

| 项 | 怎么确认 |
| --- | --- |
| 三台车的 `agvId` | `remote-ops/fleet.md` 的一一对应表。**`deviceName` 人可改且 RIoT 不保证唯一**，核对时以 CPE 地址为物理依据 |
| 服务端数据库路径 | 默认 `C:\ProgramData\8005\ControlServer\data\controlserver.db` |
| 每台车的 `slotModelVersionId` | 先跑一次 `status`（下一节），从输出里抄 |
| 现场记录与照片 | 复制 `scripts/field/templates/` 下两个模板，边做边填 |

**权威库里要先有这三台车的 IO 绑定，核对才有东西可对。**这两条命令走的是 #10 那条权威发布路径，
所以同样产版本、快照与审计；`seed-approved-facts` 是幂等的，重复跑返回既有那一版：

```bash
tools\ControlServer.FieldOps\bin\Release\net8.0\win-x64\ControlServer.FieldOps.exe seed-approved-facts --database C:\ProgramData\8005\ControlServer\data\controlserver.db
```

```bash
tools\ControlServer.FieldOps\bin\Release\net8.0\win-x64\ControlServer.FieldOps.exe bind-io --database C:\ProgramData\8005\ControlServer\data\controlserver.db --agv 老厂前线新多仓位1
```

三台车各跑一次 `bind-io`。录进去的是 REQ-0267 的**已批准**八仓事实（DO1–DO8 开锁、DI1–DI8 锁反馈、
DI9–DI16 仓内光幕、500 ms、ACTIVE_HIGH），不是现场手抄的一份——车上报的声明只被核验、永远不被采信
为权威（REQ-0258），所以没有「从车上读回来填进去」这条路。

**注意 `ACTIVE_HIGH` 这一项与票据 35 不符**：票据 35 批准的光幕是检测到物体为 `0`。`seed-approved-facts`
写进去就不可改写，所以在生产库上跑它之前先按
[`docs/defects/20260913-approved-slot-facts-call-the-light-curtain-active-high.md`](../defects/20260913-approved-slot-facts-call-the-light-curtain-active-high.md)
定处置。

先看一眼三台车此刻的判定：

```bash
tools\ControlServer.FieldOps\bin\Release\net8.0\win-x64\ControlServer.FieldOps.exe status --database C:\ProgramData\8005\ControlServer\data\controlserver.db
```

输出里每台车一行，带 `slotModelVersionId`、`ready`、`reasonCode` 与三个缺口清单
（缺绑定的仓位、缺核对的仓位、核对不通过的仓位）。`bind-io` 跑完之后的预期是 `ready=false`、
`reasonCode=SLOT_CONFIGURATION_NEVER_VERIFIED`——绑定齐了，还没核对。跑 `bind-io` 之前是
`SLOT_IO_BINDING_INCOMPLETE`。

## 二、逐台核对

一台车一个记录文件，命名 `01-agv01.json`、`02-agv02.json`、`03-agv03.json`——**脚本按文件名顺序处理**，
这个顺序就是现场推进的顺序。

每台车**八个仓位一个不漏**，每个仓位确认三个信号：开、关、到位。

**抽样会被整批拒绝。**交上来的仓位集合与该车模型的仓位集合对不上，得到的是一次拒绝，不是一份待办
清单——这是 `REQ-0263` 的「不允许抽样」在一台车之内的意思。少填一个仓位不会「先记着」，那一台车这一
轮的核对全部不作数。

三个信号里有一个没确认，这一仓算不通过：该车 `reasonCode` 会是
`SLOT_CONFIGURATION_VERIFICATION_NEGATIVE`，`slotsVerifiedNegative` 里列出是哪一仓。**修好再来一次，
不要把它填成 true。**

### 用探针逐仓核对

**为什么要绕开车载端。**这条线上车载端只在服务端授权下开锁（到站扫码、`SlotOperationCommand`、恢复向量），
服务端也只服务一台登记的车、一条连接，没有任何路径能让它「开 agv03 的 5 号仓」。所以核对时关掉车载端，由
`scripts/field/Invoke-W1SlotIoProbe.ps1` 在车上直接对 IO 模块做车载端做的那几件事：FC05 打开锁脉冲，
FC01／FC02 读输出与输入。

**人要在车前。**门弹没弹开、仓里有没有东西、门是不是手关上的，这些是 IO 看不见的物理事实；REQ-0263
明确不允许拿模拟结果冒充现场通过。探针不替任何人填 `true`。

每台车一次，控制端上跑：

```bash
pwsh -File scripts/field/Invoke-W1SlotIoProbe.ps1 -SiteAlias agv01 -Order 1 -AgvId 老厂前线新多仓位1 -IoModuleHost 192.168.71.150 -SlotModelVersionId <status 里的值> -VerifiedBy <你的名字> -RecordDirectory scripts/field/records/<日期>-W1 -PhotoPointers field-photos/<日期>-W1/agv01-panel.jpg
```

- **开跑前**：车停稳、周边无人；车载端 `SQCD.Agv.Wpf` 与 `SQCD_8005AGV_Simulator` 都在车上关掉（探针见到它们就拒绝）；
  八个仓门关着、仓内无物。
- **`-IoModuleHost` 不猜**：agv01 是 `192.168.71.150`；agv02／agv03 当天实测模块 502 端口通了再填，并回写
  `remote-ops/onboard-hmi/config/site-<别名>.json`。
- **先只读自检一次**：`pwsh -File scripts/field/Test-W1SlotIoModule.ps1 -SiteAlias agv02 -IoModuleHost <实测地址>`。
  它只读一次模块、不开锁，读到「输出全空闲、八仓锁闭、八仓无物」才退出 0；读不到、或车上开着车载端／模拟器，退出 2。
- **每仓的步骤**：确认仓门关着 → 探针打脉冲并看 3 秒 → 回答弹开的是不是本仓且只有本仓 → 放一个物体挡光幕 →
  取出 → 手关门。探针边做边读。
- **三个布尔值怎么来**：
  - 开：开锁前输出空闲且锁闭；模块接受 FC05；看到输出置位并在 3000 ms 内由模块复位；锁反馈在 3000 ms 内变为打开；
    没有别的输出或别的仓锁反馈变化；人看到本仓且只有本仓弹开。
  - 关：手关门后锁反馈稳定 300 ms 回到锁闭，所有输出空闲。
  - 到位：开锁前光幕无物；放入物体读到有物；取出后回到无物。
  - 电平按票据 35：锁闭 `1`、打开 `0`；光幕有物 `0`、无物 `1`。
- **产物**：`<Order>-<别名>.json`（交给 `verify` 的记录），`raw/<别名>/slot<n>.json`（每仓的完整读数轨迹、各项检查与现场回答）。
  记录目录只增不改，同一台车重来要换一个 `-RecordDirectory`。
- **模块没有自己复位的输出**，探针会立刻写 `0` 清掉并记为不通过：锁不能长时间通电（票据 35）。

出发前在控制端彩排一次（本机起一个模拟器、脚本冒充人，跑三台车的正例、两个反例和整个窗口脚本；**不是证据**）：

```bash
pwsh -File scripts/field/Invoke-W1SlotIoProbeRehearsal.ps1 -StageRoot <不存在的目录> -FieldOpsExecutable <self-contained 发布的 ControlServer.FieldOps.exe>
```

## 三、跑窗口

```bash
pwsh scripts\field\Invoke-W1FieldWindow.ps1 -EvidenceRoot evidence\field\20260910-W1-three-vehicle-qualification -Database C:\ProgramData\8005\ControlServer\data\controlserver.db -RecordDirectory scripts\field\records\20260910
```

生产库在厂区服务器上，而那台机器没有这个仓库、也没有 git 和 SDK。在那里跑时，把脚本、记录目录和
self-contained 发布的 FieldOps 拷过去，显式给出 FieldOps 构建自哪个 commit：

```bash
pwsh -File Invoke-W1FieldWindow.ps1 -EvidenceRoot <不存在的目录> -Database C:\ProgramData\8005\ControlServer\data\controlserver.db -RecordDirectory <记录目录> -FieldOpsExecutable <发布目录>\ControlServer.FieldOps.exe -ControlServerCommit <commit>
```

`-EvidenceRoot` **必须是不存在的目录**。脚本会：

1. 记一次开工前的三车快照；
2. 逐台 `verify` → `release` → 再记一次三车快照；
3. **第一台车放行之后**记门禁启用审计；
4. 收尾导出本窗口的不可改写审计与最终快照；
5. 算判据，写 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。

## 四、门禁启用这一步实际做了什么

`enable-gate` 写的是**一条不可改写审计和一个时刻**，好让「门禁上线晚于第一台车核对通过」这件事在
证据里可复核。**它自己不拦车。**

要让门禁真的生效，运维改配置并重启服务：

```
Governance:slotConfigurationReadinessGate = Enforcing
```

缺省是 `Off`。写成别的值服务启动即抛——一个被拼错的档位名如果被静默当成关的，现场会以为门禁开着。

还有一句必须说清：**当前 `src/` 里没有任何生产调用点读
`SlotConfigurationReadinessGate.IsSlotConfigurationConfirmedAsync`。**把 readiness 接进投运判定属于
投运流程，不属于本窗口。所以本窗口证明的是「三台车的 readiness 是怎么取得的」，不是「门禁在拦车」。

## 五、出了问题怎么办

| 情况 | 怎么做 |
| --- | --- |
| 某台车核对被拒（抽样） | 补齐那台车的记录文件，**写到一个新的证据目录**重跑。红的那份留着 |
| 某台车有仓位不通过 | 现场修接线／IO，改完这台车重新逐仓核对。**其它两台不受影响**——门禁是每车判定 |
| 硬件动过 | 那台车的核对整批作废，`reasonCode` 变 `HARDWARE_CHANGED_REVERIFICATION_REQUIRED`，重新逐仓核对 |
| 脚本中途失败 | 证据目录原样留着，不要删。纠正写新目录，并在新的 `SUMMARY.md` 里指向被纠正的那一份 |

**红的证据不允许被绿的重跑覆盖。**这条没有例外。

## 六、出口判据

三台车各自取得 `SlotConfigurationReadiness` 且审计留痕。脚本的六条判据覆盖它：逐仓无抽样、逐台放行、
门禁上线晚于第一台通过、零停产、逐台审计留痕、证据形状完整含照片指针。

零停产那一条一半看服务端自己的 ready 序列，一半看现场逐台记下的「车队仍在接单」——后者是人看到的
事实，必须现场当时填，事后补填等于没有。
