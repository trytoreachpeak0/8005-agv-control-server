# 缺陷：L2 驱动的 `Confirm()` 要求车载端窗口在前台，正式 journey G3 因此丢了两条场景

Status: fixed（L2 编排器，非产品）
Owner repository: `8005-agv-control-server`（`scripts/l2/L2.psm1`）
Found by: 2026-09-14 正式 journey G3 运行（服务端 `1b1f3dd7`，车载端 `b960108`，runner `02cad683`；证据 `C:\g3dbg\formal-journey-1b1f3dd7`，未入库，原样保留）
Fixed in: 见提交记录（本单与修复同一提交）

**红在编排器，不在产品。**两端代码在这次运行里没有任何一步做错；是驱动没把「是」按下去。

## 现象

十条场景里八条跑完且判据全过，`g3-pickup-load-and-correction` 与 `g3-load-cancellation` 在按确认框时中止：

```
Exception calling "Confirm" with "1" argument(s): "Exception calling "Invoke" with "0" argument(s): "Operation is not valid due to the current state of the object.""
```

两条场景只在 11:40 前后各跑了约 30 秒。那段时间桌面前台是另一个应用的弹窗（本会话向用户提问的选择框），车载端的确认框不在前台。
中止使 `noScenarioAbortedBeforeItsJudgments` 失败；它归每一片，所以 `FP-IS-01/02/03/07` 四片同判 `FAIL`。

## 原因

`Confirm()` 只调 UIA `InvokePattern.Invoke()`。MessageBox 的按钮在对话框没拿到前台时拒绝 Invoke，车载端收不到点击。
`FP-IS-07` 的场景在车载端重启后撞到过同一件事，已在 `scenarios/G3RecoveryCommon.ps1` 的 `Invoke-G3DialogButton` 里兜底；公共驱动没跟上。

## 修复

`Confirm()`：Invoke 失败就向按钮的 Win32 窗口投递 `BM_CLICK`（异步，不要求前台）；并且要等对话框真的关掉才返回，15 秒不关就抛出。
Invoke 成功的路径行为不变，只多了「确认关掉」这一步。

## 证据

| 项 | 结果 |
| --- | --- |
| 修复后调试运行 `g3-load-cancellation`（证据 `C:\g3dbg\confirm-fallback-cancel-001`，未入库） | 7/7 `PASS`；车载端窗口在前台，Invoke 直接成功，没有走到 `BM_CLICK`——证明正常路径没被「等对话框关掉」改坏 |
| 投递 `BM_CLICK` 这条路径 | 与 `G3RecoveryCommon.ps1` 的 `Invoke-G3DialogButton` 同一个做法，那边在 `resume-008` 等运行里真实走到过并通过 |
| 正式 journey G3 | 共享绑定挪到含本修复的提交后重跑，结果记在 `evidence/g3/` 的 SUMMARY |
