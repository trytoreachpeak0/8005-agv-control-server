# control-server#218（批次7-15）证据

本机真装置（控制端笔记本，车载端 WPF + slots-simulator，回环假 RIoT 与假 MesIngest），2026-09-22 凌晨调度批的第一段时段。
每遍只留 `assertions.json`、`SUMMARY.md`、`timeline.jsonl`，构建与进程日志不入库（每遍 7～15 MB）。三端提交取自各遍
`assertions.json` 的 `identity`，协议都是 `protocol-v2.0.0@86575456`。

这些是**调试与红证据**，不是正式证据：正式的真装置与 G3 证据在批次7-18（control-server#220）。

## 目录

| 目录 | 场景 | 服务端 | 车载端 | 结果 |
| --- | --- | --- | --- | --- |
| `red/g3-claims-offline/` | `Test-G3RunnerClaims.ps1` 离线 | `cfaf0fd9`（只有认领） | — | 红：`drives 'g3-multi-stop-plan', which has no scenario script` |
| `uia-probe/` | 同形 XAML 的 UIA 实测 | — | — | 计划腿行的 `ItemStatus` 不在 UIA 树上（`docs/defects/20260922-journey-plan-legs-item-status-not-in-uia-tree.md`） |
| `green/mso-003/` | `real-onboard-mixed-side-one-stop` | `fbe7f239` | `deeba94c` | PASS 12/12 |
| `red/red-unload-order/` | 同上 | `b43fa390`（`6bf3ee16` + `patches/server-unload-order.patch`：卸货按加入先后**倒序**） | `deeba94c` | 只红 `L2-MSO-10`：卸货先后 `REAR,FRONT,FRONT`，丙的卸货命令早于乙的卸货提交，采样里丙仓先开 |
| `red/red-slot-group/` | 同上 | `30207876`（`6bf3ee16` + `patches/server-slot-group.patch`：全部派进前侧组） | `deeba94c` | 红 `L2-MSO-05`、`06`、`09`：丙落在 3 号仓（前侧）；物理状态与目标仓照样一致，是「目标仓在区域指派的那一组」那一半红的 |
| `red/red-revision/` | `g3-multi-stop-plan` | `fe6783d5`（`6bf3ee16` + `patches/server-revision.patch`：重发的计划不升修订号） | `deeba94c` | 红：没有一版三条腿的计划被车载端确认，`G3-08-01`～`04`、`06`、`07` 记「未到达」 |
| `red/red-hmi-reorder/` | `g3-multi-stop-plan` | `6bf3ee16` | `a7abe3aa`（`deeba94c` + `patches/onboard-plan-reorder.patch`：计划腿按站名本地排序） | 红 `G3-08-06`：界面行序 `2,1,3`。另外 `G3-08-05` 也红、场景末尾一行抛错，**与注入无关**，见下 |

缺陷提交都在本地分支（服务端 `local/b7-15-defect-*`，车载端 `w2g/b7-15-local-plan-reorder-defect`），从未推送，补丁在 `red/patches/`。

## 调试过程里红过的遍次（证据不入库，只记原因）

- `mso-001`（`939da869`）：乙丙在车开往 11 号站途中发布，两分钟没进旅程，积压 `ONBOARD_FACTS_NOT_READY`。真车载端在车有未结束的 RIoT
  单时报「是否停车未知」，会话 `RecoveryRequired`，在途判据读不到车载端事实。按用户 09-22 决定，途中追加只在停站时（control-server#286、
  program#133），这是预期行为；场景改为车停在站上时追加。
- `mso-002`（`8da49a04`）：场景脚本错误，`[Nullable[DateTimeOffset]]` 在 PowerShell 里赋值即解包，没有 `.Value`。
- `msp-001`（`fbe7f239`）：`G3-08-04` 把「先作废、后确认」的一版计划判红——离开最后一个装货站时「开往关卡」那一版被「已到关卡」那一版
  在几十毫秒内退役（`RetireSupersededSnapshotAsync`），车载端随后仍确认了它；判据改为「作废过的那份后面必须有同一流更高号的一份」。
  `G3-08-06` 读不到行序：重建后的行里取第一个文字是空白。
- `msp-002`（`d90ef273`）：`G3-08-06` 同上，这次行里一个文字元素都没有；序位改为取纯数字的那段文字，没有就退到 DataItem 名称。
- `msp-003`（`6bf3ee16`）PASS 7/7，但 `G3-08-05` 之后改了读的时机（下一节），所以这一遍不作为最终绿证据，第二段时段重跑。

## `red-hmi-reorder` 里与注入无关的那一处

这一遍派车那一版计划到车到站时才发出（`18:42:47`，受理在 `18:41:15`）：受理之后车载端立刻报了「有未结束的单」，会话转 `RecoveryRequired`，
快照压到会话回 `Ready` 才发。另三遍里派车计划都赶在那份报告之前发出。`G3-08-05` 原来读「派车那一版」，于是判的是一场与本条无关的竞速。
已改为车停在 12 号站、甲装完之后读追加前最后一版（`cecdf676`）；末尾那一行在 `$dispatchPlan` 为空时抛错，一并改掉。
