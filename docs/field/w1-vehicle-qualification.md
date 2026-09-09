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

## 三、跑窗口

```bash
pwsh scripts\field\Invoke-W1FieldWindow.ps1 -EvidenceRoot evidence\field\20260910-W1-three-vehicle-qualification -Database C:\ProgramData\8005\ControlServer\data\controlserver.db -RecordDirectory scripts\field\records\20260910
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
