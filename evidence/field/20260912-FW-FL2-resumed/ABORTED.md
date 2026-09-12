# 现场窗口二（无人）续跑：CH 做不成，收尾时没有 finalize

2026-09-12 15:02–16:30（本地时间），`agv01`，服务端 `e0d6df7`、车载端 `6b8a0b0`、`protocol-v0.3.0`。
驱动与采集器所在提交是 `8005-agv-control-server` `14e39fa`。
授权记录见 [#20 留言](https://github.com/trytoreachpeak0/8005-agv-program/issues/20#issuecomment-5644408526)。另外两次派车，用户是在对话里单独同意的：原话「可以派车充电」「可以派车」。

**这个目录不是一次完成的窗口。**自动充电在现场根本做不成（缺陷 `docs/defects/20260912-auto-charging-never-charges-in-field.md`），
驱动脚本停在「等充电行程释放」这一步，被手动停掉。所以它没写现场记录，采集器也没有 finalize，目录里没有 `assertions.json` 与 `SUMMARY.md`。
下面每一条都能在 `logs/` 的原始输出或 `snapshots/` 的库行里对上。

## 场景结论（人工对照原始输出，不是采集器判据）

| 场景 | 结论 | 出处 |
| --- | --- | --- |
| S52（#52 修复的现场观测） | **成立**。15:18:24 开门，15:18:34 旅程 `bb16f190` 离开 `2/AwaitingDepartureSafety` 去 `3/AwaitingPickupArrival`。停靠 2 的检查 id `55627923` 换成 `6cc361d1`，消费的回答是 `e965fdff` | `logs/driver-console.log`、`snapshots/02-resumed-departed` |
| T、X | 上次中止窗口已演过，本窗续跑同一趟旅程，没有重演 | `../20260912-FW-FL2-unattended/carried-acts.json` |
| 第一趟装货 | 停靠 3（`Q26091208-17`，仓 5、6）15:23:02 提交；停靠 4（`Q26092108-38`，仓 7、8）15:27:50 提交 | `logs/driver-console.log` |
| NE | **成立**。关卡上仓 5 带货关门两轮，车每轮自己重开（`UNLOCKING` 1→2→3），15:30:48 抓帧；取空后卸货提交。旅程在服务端 15:31:58 Completed | `snapshots/03-j1-ne-reopened` |
| R1 | **成立**。守望在 Completed 后 6 ms 停服务，`-Off` 重启；15:32:03 起会话 generation 438、439 为 `Ready` | `logs/gate-watch-journey1.log`、`snapshots/04-r1-ready` |
| 第二趟（计划外） | 本该先充电。15:38:04 引擎找不到充电桩，15:38:06 却受理了旅程 `afc0ce0a`（5 个取货站）。驱动脚本不管这一趟，于是 15:45 起由临时接管脚本照常装卸：停靠 1 在站点期限之前录入 SUBLOT，5 站全部装货提交，关卡 5 次卸货全部提交，16:05:59 Completed。**没有需求被期限结算或抑制** | `logs/journey2-takeover.ps1`、`logs/journey2-takeover-console.log` |
| R2 | **成立**。Completed 后 7 ms 停服务；16:06:05 会话 generation 441 为 `Ready`；门关上，临时充电线撤掉 | `logs/gate-watch-journey2.log`、`snapshots/05-r2-ready` |
| CH | **不成立**。补演时充电行程 `bc7244b5` 在 67% 触发、去 211，16:23:46 进入 `Charging`，16:24:06 起 `CHARGER_NOT_ENGAGED`，RIoT 一直是 `NO_CHARGE`。用户在现场确认：RIoT 订单只有移动动作，没有充电动作 | `snapshots/06-charge-dispatched`、`07-charge-charging`、`08-aborted-charger-not-engaged` |

## 充电这一幕依次踩到的三层

1. **15:38 第一次派车**（临时充电线 76 / 80）。日志：

   ```
   [2107] Charger station could not be resolved on the current map ... Map 25 does not contain exact fixed station 充电准备点1/211.
   ```

   当时 RIoT 地图上 211 叫「站点211」。充电行程没能起来，引擎在同一轮照常接单，车直接去了产线。用户在现场发现后告知。
2. **用户在 RIoT 把 211 改名为「充电点1」**，16:16 读到地图上 211 叫「充电点1」、212 叫「充电准备点1」。
   经用户同意，在 factory01 的 `appsettings.Production.json` 里加了 `JourneyRuntime.chargerStationId=充电点1`：16:12 先写成「站点211」，16:17 改为「充电点1」，逐字节与地图一致。改前的备份是 `*.bak-*-charger`。
3. **16:20 第二次派车**（临时充电线 68 / 72）。充电行程这次起来了，车也到了 211，但接不上电：`TO_CHARGER` 单和取货单一样走 `CreateMoveOrderAsync`。

## 收尾（16:27–16:30）

- 16:27:56 执行 `Set-JourneyRuntime.ps1 -Off`：两个门关闭，临时充电线撤回出厂的 20 / 80。`chargerStationId=充电点1` 这条覆盖**保留**，它是正确的现场值。
- 停主驱动、监视器与 RIoT 轮询。16:28 抓 `08-aborted-charger-not-engaged`。
- 16:29 执行 `10-start-onboard-stack.ps1 -Stop`；16:30 执行 `15-set-automation-face.ps1 -Revert`，其余 93 个叶子不变。
- factory01 上守望进程 0 个。

## 留下的现场

- **充电行程 `bc7244b5` 仍是 `Charging / CHARGER_NOT_ENGAGED`，库里没有结束它的产品路径。**下次开门时引擎会一直卡在这条行程上：电量低于恢复线 80，又没在充电，就永远不释放，也不再接单。开窗前必须先把它结算掉。
- 车停在 211，16:24 读到 66%、`NO_CHARGE`。
- 旅程 `bb16f190`、`afc0ce0a` 都已 Completed，租约已释放。
