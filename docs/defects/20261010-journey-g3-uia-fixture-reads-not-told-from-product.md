# journey G3 第三轮两个场景红在界面读写夹具：读不到与产品结果没有分开

Found by: [`evidence/g3/20261010-protocol-v3.0.0-journey-1f63fe0b/`](../../evidence/g3/20261010-protocol-v3.0.0-journey-1f63fe0b/)（control-server#393 出口第三轮 G3，绑定提交 `b6be67ac`，`JOURNEY_G3_SLICE_FAIL`，20 个场景中 18 个 PASS）

## 背景

批次 8 出口的 G3 一共跑了四轮。第一、二轮红在从没真跑过的脚本分支上（[`20261010-journey-g3-two-scenarios-first-run-stale-scripts.md`](20261010-journey-g3-two-scenarios-first-run-stale-scripts.md)）。第三轮把那两处修好后，又红了两个**前两轮都 PASS** 的场景：`g3-multi-stop-plan`（`FP-IS-08`）和 `g3-waiting-point-idle-return`（`FP-IS-12`）。三轮之间 `src/` 零差异（`git diff 5f3adc42 1f63fe0b -- src tests tools` 为空）。

这两处都没有找到产品状态出错的证据。共同点是：脚本经 UI Automation 读写车载端界面时出了一次差错，而脚本把它当成了产品给出的结果。

## 一、`g3-multi-stop-plan`：车载端本地拒收了 B 的子批

**现象（读到的）**：同一取货站装两条需求。A 装完后，脚本给 B 录入子批并点「手动提交」，车载端在本地拒收：

```
2026-10-10T13:03:54.8491938+08:00	Warning	MainViewModel	界面命令被业务规则拒绝：SUBLOT_NOT_IN_WORKLIST。
```

意思是：车载端认为这个子批不在当前清单里，所以没有发给服务端。服务端因此一直没有下 B 的装货命令，脚本等 180 秒超时。

**排除了什么（读到的）**：「新清单晚于录入到达」这条竞态可以排除。05:03:54.262（UTC）服务端排出只含 B 的清单 r2，.271 排出录入请求 r2，.288 车载端回了 `SnapshotAppliedAck`，.849 才在本地拒收。车载端的投影是同步应用的（车载端 `WireToGateSessionClient.cs:3016-3030`）。

**剩下的解释（推的）**：把判据代进车载端 `WireToGateBusinessService.cs:807-822`，唯一能让它拒收的是：点提交那一刻，扫码框里的文本不是 B。证据里没有记录框内文本，所以定不了。当时桌面偏慢，UIA 每次调用约 2 秒。

**脚本的缺口（读到的）**：修复前的 `MultiStopRigCommon.ps1:83-116` 提交前不读回 `ScanText()`，提交后也不看车载端有没有拒收，只等服务端的装货命令。

## 二、`g3-waiting-point-idle-return`：持续断言读到一次「空」就判红

**现象（读到的）**：G3-12-03 要求界面在点上报 `AT_WAITING_POINT`，并在之后十秒内一直如此。05:07:35.03 读到 `AT_WAITING_POINT`，38.54 读到空。下一张快照 40.606 才排出，这段时间车载端零日志、没有重连，产品侧没有任何东西会改这个值。

**脚本的缺口（读到的）**：读数函数 `Get-L2LoadingPhaseLine`（`L2MultiStopJourney.psm1`）在元素找不到或抛 `ElementNotAvailableException` 时返回 `$null`，汇总里显示得和空串一样。十秒持续断言读到一次不是 `AT_WAITING_POINT` 就判失败（修复前的 `g3-waiting-point-idle-return.ps1:191-194`）。另外，G3-12-07 用的是窗口之前的第一次读数，不是窗口里的读数。

## 去向

control-server#560（PR #561，合并提交 `52056c48`）只改 `scripts/`。调度定的原则是：**夹具读写界面失败，可以在夹具层重读或重输，并且每次都记录；产品给出的结果不重试、不放宽。**

- A：提交前读回框内文本并记录；文本不对，算输入没进去，重输最多 2 次；文本对了只提交一次。车载端拒收就以 `PRODUCT_REFUSED` 失败，失败信息带框内文本、拒收码和时刻，不重提。
- B：把「元素不可读」和「读到的值」分开。不可读重读最多 3 次、间隔 0.5 秒；读到的值（包括空串）原样当作产品结果。G3-12-07 改用窗口里的读数。
- 离线自检 `scripts/l2/Test-G3JourneyUiaFixtureReads.ps1`：12 条，修复前红 11 条，7 个变异全部被杀（[`evidence/l2/cs560-journey-uia-fixture-reads/`](../../evidence/l2/cs560-journey-uia-fixture-reads/)）。

第四轮（绑定 `76c9cfe2`，[`evidence/g3/20261010-protocol-v3.0.0-journey-76c9cfe2/`](../../evidence/g3/20261010-protocol-v3.0.0-journey-76c9cfe2/)）20/20 PASS。**新加的重试一次也没有触发**：每次录入第一次读回就是正确的子批，只提交一次，也没有出现读不到的记录。所以第三轮的两处没有复现，cs#560 的重试路径在真装置上还没被走到过，它的正确性目前只由离线自检和变异证明。下次如果再出现，时间线会记下读回文本、拒收码与时刻。

本轮的红证据原样保留（这两个红场景整套入库，其余场景按出口报告「证据精简」一节只留三份文件）。
