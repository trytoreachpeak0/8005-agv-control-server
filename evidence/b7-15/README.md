# control-server#218（批次7-15）证据

本机真装置（控制端笔记本，车载端 WPF + slots-simulator，回环假 RIoT 与假 MesIngest），2026-09-22 凌晨调度批的第一段时段。
每遍只留 `assertions.json`、`SUMMARY.md`、`timeline.jsonl`，构建与进程日志不入库（每遍 7～15 MB）。三端提交取自各遍
`assertions.json` 的 `identity`，协议都是 `protocol-v2.0.0@86575456`。

这些是**调试与红证据**，不是正式证据：正式的真装置与 G3 证据在批次7-18（control-server#220）。

## 目录

| 目录 | 场景 | 服务端 | 车载端 | 结果 |
| --- | --- | --- | --- | --- |
| `red/g3-claims-offline/` | `Test-G3RunnerClaims.ps1` 离线 | `cfaf0fd917fb5e95f7bf7b621f36df2cc11c5a85`（只有认领） | — | 红：`drives 'g3-multi-stop-plan', which has no scenario script` |
| `uia-probe/` | 同形 XAML 的 UIA 实测 | — | — | 计划腿行的 `ItemStatus` 不在 UIA 树上（`docs/defects/20260922-journey-plan-legs-item-status-not-in-uia-tree.md`） |
| `green/mso-003/` | `real-onboard-mixed-side-one-stop` | `fbe7f239556101395bc7980ff0339168708d5f5c` | `deeba94c42c51561621634194d3c9f6582737486` | PASS 12/12 |
| `red/red-unload-order/` | 同上 | `b43fa390ce616913f13af0c04b34e39e61b3d9b1`（`6bf3ee1653b290de66890b32791f70d6faa071f5` + `patches/server-unload-order.patch`：卸货按加入先后**倒序**） | `deeba94c42c51561621634194d3c9f6582737486` | 只红 `L2-MSO-10`：卸货先后 `REAR,FRONT,FRONT`，丙的卸货命令早于乙的卸货提交，采样里丙仓先开 |
| `red/red-slot-group/` | 同上 | `30207876fb3b58fc740e91775c3a8c7691f1c01f`（`6bf3ee1653b290de66890b32791f70d6faa071f5` + `patches/server-slot-group.patch`：全部派进前侧组） | `deeba94c42c51561621634194d3c9f6582737486` | 红 `L2-MSO-05`、`06`、`09`：丙落在 3 号仓（前侧）；物理状态与目标仓照样一致，是「目标仓在区域指派的那一组」那一半红的。`09` 也红，是因为车载端清单行的侧标跟着那条需求仓位命令的目标仓组走（onboard-hmi#134 的 `WorklistItemSides`），丙的命令指向前侧仓，侧标就读成 `FRONT` |
| `red/red-revision/` | `g3-multi-stop-plan` | `fe6783d5f7d4d639a6f384d2b74b4a47df9510fb`（`6bf3ee1653b290de66890b32791f70d6faa071f5` + `patches/server-revision.patch`：重发的计划不升修订号） | `deeba94c42c51561621634194d3c9f6582737486` | 红：没有一版三条腿的计划被车载端确认，`G3-08-01`～`04`、`06`、`07` 记「未到达」 |
| `red/red-hmi-reorder/` | `g3-multi-stop-plan` | `6bf3ee1653b290de66890b32791f70d6faa071f5` | `a7abe3aab0106a592f88340a53009b8ab6119d9b`（`deeba94c42c51561621634194d3c9f6582737486` + `patches/onboard-plan-reorder.patch`：计划腿按站名本地排序） | 红 `G3-08-06`：界面行序 `2,1,3`。另外 `G3-08-05` 也红、场景末尾一行抛错，**与注入无关**，见下 |

缺陷提交都在本地分支（服务端 `local/b7-15-defect-*`，车载端 `w2g/b7-15-local-plan-reorder-defect`），从未推送，补丁在 `red/patches/`。

## 调试过程里红过的遍次（证据不入库，只记原因）

- `mso-001`（`939da869e980c2142d0f8021316c96dfbc1843c6`）：乙丙在车开往 11 号站途中发布，两分钟没进旅程，积压 `ONBOARD_FACTS_NOT_READY`。真车载端在车有未结束的 RIoT
  单时报「是否停车未知」，会话 `RecoveryRequired`，在途判据读不到车载端事实。按用户 09-22 决定，途中追加只在停站时（control-server#286、
  program#133），这是预期行为；场景改为车停在站上时追加。
- `mso-002`（`8da49a04f6206c9299ed3634b4b8ce05c327ed50`）：场景脚本错误，`[Nullable[DateTimeOffset]]` 在 PowerShell 里赋值即解包，没有 `.Value`。
- `msp-001`（`fbe7f239556101395bc7980ff0339168708d5f5c`）：`G3-08-04` 把「先作废、后确认」的一版计划判红——离开最后一个装货站时「开往关卡」那一版被「已到关卡」那一版
  在几十毫秒内退役（`RetireSupersededSnapshotAsync`），车载端随后仍确认了它；判据改为「作废过的那份后面必须有同一流更高号的一份」。
  `G3-08-06` 读不到行序：重建后的行里取第一个文字是空白。
- `msp-002`（`d90ef27373d5915c8101975fbb8dde5198f9ec5d`）：`G3-08-06` 同上，这次行里一个文字元素都没有；序位改为取纯数字的那段文字，没有就退到 DataItem 名称。
- `msp-003`（`6bf3ee1653b290de66890b32791f70d6faa071f5`）PASS 7/7，但 `G3-08-05` 之后改了读的时机（下一节），所以这一遍不作为最终绿证据，第二段时段重跑。

## `red-hmi-reorder` 里与注入无关的那一处

这一遍派车那一版计划到车到站时才发出（`18:42:47`，受理在 `18:41:15`）：受理之后车载端立刻报了「有未结束的单」，会话转 `RecoveryRequired`，
快照压到会话回 `Ready` 才发。另三遍里派车计划都赶在那份报告之前发出。`G3-08-05` 原来读「派车那一版」，于是判的是一场与本条无关的竞速。
已改为车停在 12 号站、甲装完之后读追加前最后一版（`cecdf6762c1ffd308b5da9210c365454eba0cbbf`）。这场竞速在正常车载端上同样会发生，所以是场景本身的不稳，修在绿的一边；注入只改显示顺序，预期只红 `G3-08-06`，第二段在最终 head 上重跑这份红证据来证；末尾那一行在 `$dispatchPlan` 为空时抛错，一并改掉。

## 最终证据（第三段，2026-09-22 03:43～04:08，本机）

本票最终 head 是 `23c846bf4f2d5ce39d7f10269796d9756d08e70d`（此后只有证据与文档提交）。三端：服务端 `23c846bf4f2d5ce39d7f10269796d9756d08e70d`、
车载端 `deeba94c42c51561621634194d3c9f6582737486`、模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`。上面第一段的 `mso-003`、`msp-*` 与四份红证据早于
`G3-08-05` 改时机、名称兜底修正（`fcbaf57b`）与基线仓位前置检查，留作过程记录；**以本节为准**。

| 目录 | 场景 | 车载端 | 结果 |
| --- | --- | --- | --- |
| `green/s3-mso/` | `real-onboard-mixed-side-one-stop` | `deeba94c` | PASS 12/12 |
| `green/s3-msp/` | `g3-multi-stop-plan` | `deeba94c` | PASS 7/7 |
| `red/s3-red-hmi-reorder/` | `g3-multi-stop-plan` | `a7abe3aab0106a592f88340a53009b8ab6119d9b`（`patches/onboard-plan-reorder.patch`） | 只红 `G3-08-06`：界面行序 `2(name),1(name),3(name)`；三行的序位都走名称兜底取出（行里没有文字元素），兜底在真装置上确实走到 |

G3 runner 自检（证据不入库，在控制端 `C:\Users\szy\b715\`）：

| runner | 服务端 | 车载端 | 结论 |
| --- | --- | --- | --- |
| `run-journey-g3.ps1`（15 场景，`-SelfCheckControlServerCommit`、`-SelfCheckOnboardCommit`） | `23c846bf` | `deeba94c` | `JOURNEY_G3_PASS`，`FP-IS-01/02/03/07/08/10/11` 全 PASS，两个 commitSource 都是 `SELF_CHECK_OVERRIDE`（`s3-sc-journey`） |
| `run-staged-g3.ps1`（从本票 head 跑，绑定原样） | `85381ea2a37e46b4c720ff5f1843161ad6deb69d` | `4d716340982de4e39339c2151c291efe1a21e1d1`（detached worktree，`-OnboardRemoteRef` 传 sha，自检时有意跳过「须为顶端」） | `STAGED_G3_RECOVERY_REPLAY_PASS`，`FP-IS-00/06/07/14/15` 全 PASS（`s3-sc-staged`） |
| `run-staged-g3-restart.ps1`（不推送的临时绑定提交 `3d6e8584`） | `b621a97bfb5feb7f941e8d25e82652f456d1ed7f` | `deeba94c` | `STAGED_G3_PROCESS_RESTART_PASS`（`sc-restart`） |
| `run-demand-bearing-g3-vectors.ps1`（同上） | `b621a97b` | `deeba94c` | `DEMAND_BEARING_G3_VECTORS_PASS`（`sc-demand`） |

`b621a97b` 之后认领表（`g3-slice-evidence.ps1`）与后两个 runner 一字未动，所以没有在 `23c846bf` 上重跑它们。staged 在**移过的**绑定
（`b621a97b` + `deeba94c`）上中止（恢复探针 `forced-mechanical-command-replayed-after-reconnect` FAIL、槽位配置激活 409，runner 随后在 IndexOf 上抛 null
盖住原错误，`C:\Users\szy\b715\sc-staged\`）：与认领无关，调度已另开票，出口票移绑定前要先解决。

## 审查后的一处修正（证据之后）

独立审查指出 `g3-multi-stop-plan.ps1` 末尾那行在 `$dispatchPlan` 为空时写的是 `else { ? }`：`?` 是 `Where-Object` 的别名，不带参数调用
会停在参数提示上（经 runner 的 `& pwsh … 2>&1` 启动时提示不可见，进程挂住、桌面锁不释放）。已改为 `'?'`，并按 AST 扫了本票全部
`.ps1`/`.psm1`，不带参数的别名调用为 0（`alias-scan/`，含对修正前那一版的正向对照）。

按调度决定没有重跑真装置：三段所有 PASS 与红证据里 `G3-08-05` 都判过（`$dispatchPlan` 非空），没有一遍走到这一行——
`green/s3-msp`、`red/s3-red-hmi-reorder`、`red/red-revision` 的 `G3-08-05` 都是 PASS，第三段 journey 自检里这条场景也 PASS。
