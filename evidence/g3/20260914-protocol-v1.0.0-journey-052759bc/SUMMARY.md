# `G3`（journey runner）：`FP-IS-01`／`02`／`03`／`07` **全部 `PASS`**，第一次有 G3 面

`JOURNEY_G3_PASS`，`assuranceLevel` 为 `JOURNEY_SIMULATED_COUNTERPARTS`，80 条断言全绿，四片均为 `formalSlicePass: true`。本轮没有带 `-Slice`。

**这四片此前在任何 G3 runner 里都不出现**（2026-09-09 起记在 `slicesWithoutSurfaceThisBatch`）。本轮四个 runner 的 `slicesWithoutSurfaceThisBatch` 都是空的。

## 这个 runner 测什么

真服务端、真车载端（WPF，由 UI Automation 驱动操作员动作）、真仓位模拟器（Modbus），RIoT 与 MES 用编排器自带的仿真对端。
十条 L2 场景依次跑，每条场景的判据由 `scripts/run-journey-g3.ps1` 映射成 G3 断言，再按 `scripts/g3-slice-evidence.ps1` 的归属表分到各片。
「真跑、对端仿真」算正式切片通过，等级名 `JOURNEY_SIMULATED_COUNTERPARTS`，是用户 2026-09-13 的裁定。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `052759bca58a04316dfda260249b3b5697f2cf8e`（`SHARED_BINDING`） |
| `onboardHmi` | `b96010825d43aeee3b861cb3b4716f4d0873c8a0`（`origin/w2g/b3-on-v2` 的顶） |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793`（`protocol-v1.0.0`，`APPROVED_RELEASE`） |
| `runner` | `6532ec139b171ddd8fadb0723e04bb68248e6114`，`runnerWorktreeCleanAtStart: true`（从本地仓干净克隆 `C:\s3\runner-6532ec13` 启动） |

## 结论

| 片 | 状态 | 断言 | 向量 |
| --- | --- | --- | --- |
| `FP-IS-01` | **`PASS`** | 18 | `CV-DEMAND-ACCEPT-TO-PICKUP` |
| `FP-IS-02` | **`PASS`** | 28 | `CV-PICKUP-SUBLOT-LOAD`、`CV-LOAD-CORRECTION`、`CV-LOAD-CANCELLATION-ALL-EMPTY` |
| `FP-IS-03` | **`PASS`** | 20 | `CV-PREDEPARTURE-SAFETY-EXPIRES`、`CV-OPERATION-RESULT-UNKNOWN-RECONCILE` |
| `FP-IS-07` | **`PASS`** | 38 | `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`、`CV-EXCEPTION-RESUME`、`CV-EXCEPTION-COMPENSATE`、`CV-FAULT-CARGO-HANDOFF`、`CV-FORCED-MECHANICAL-RECOVERY`、`CV-MANUAL-CHARGING-RETURN` |

每片的断言数 = 该片自己的场景判据 ＋ 8 条运行级断言（精确克隆、tag 指向绑定提交、每条场景跑的都是绑定的服务端／协议发布／真车载端／绑定的对端、无场景中止、密钥扫描）。
场景判据共 72 条，加 8 条运行级，即 `run-result.json` 的 80 条。

| 场景 | 退出码 | 断言号 | 片 |
| --- | --- | --- | --- |
| `g3-journey-demand-to-pickup` | 0 | G3-01-01～10 | `FP-IS-01` |
| `g3-pickup-load-and-correction` | 0 | G3-02-01～13 | `FP-IS-02` |
| `g3-load-cancellation` | 0 | G3-02-21～27 | `FP-IS-02` |
| `g3-predeparture-check-expires` | 0 | G3-03-01～07 | `FP-IS-03` |
| `g3-operation-result-unknown-reconcile` | 0 | G3-03-08～12 | `FP-IS-03` |
| `g3-exception-resume` | 0 | G3-07-01～11 | `FP-IS-07` |
| `g3-exception-compensate` | 0 | G3-07-21～25 | `FP-IS-07` |
| `g3-fault-cargo-handoff` | 0 | G3-07-31～35 | `FP-IS-07` |
| `g3-forced-mechanical-recovery` | 0 | G3-07-41～45 | `FP-IS-07` |
| `g3-manual-charging-return` | 0 | G3-07-51～54 | `FP-IS-07` |

## 这四片的 G3 面是怎么补出来的

真车载端场景撞出的产品缺陷都先修在两端，修复前红／修复后绿的测试在各自的 G2 里：

- 服务端：离站等待（`6e8dea5a`）、到站前先发计划（`5293c43f`）、迟到结果不重开已取消装载（`1312a512`）、出发前检查过期后重问（`6c252816`、`11f3d69d`）、
  状态未知结果跨会话对账（`e62b136d`）、恢复原操作带原命令哈希（`d2a19c7a`）、恢复收尾后回到就绪（`e6b92ee3`）。缺陷单在 `docs/defects/2026091[34]-*`。
- 车载端（`w2g/b3-on-v2`）：出发前检查过期回 `PREDEPARTURE_CHECK_EXPIRED`、状态未知结果记待结清并在报告确认后补发（`3fb8a6e`）、
  「强制机械恢复」「充电后返回服务」两个操作员入口（`f14f8af`，用户裁定补界面入口）。

## 本轮之前那一次失败

同日共享绑定先指向 `1b1f3dd7`（runner `02cad683`）跑过一次，结果 `JOURNEY_G3_SLICE_FAIL`，证据原样留在本机 `C:\g3dbg\formal-journey-1b1f3dd7`，未入库。
八条场景判据全过；`g3-pickup-load-and-correction` 与 `g3-load-cancellation` 在按确认框时中止——车载端确认框不在前台，L2 驱动的 `Confirm()` 只会 UIA Invoke，被拒。
`noScenarioAbortedBeforeItsJudgments` 归每一片，所以四片同判 `FAIL`。**红在编排器，不在产品**，见 `docs/defects/20260914-l2-confirm-needs-foreground.md`。

修复在 `052759bc`（`src/`、`tests/` 相对 `1b1f3dd7` 零差异），绑定随之挪过来，四个 runner 与 `CONTROL_SERVER_G2` 全部在这个提交上重跑。本轮十条场景都没有中止。

## 未在本轮证明的

- **RIoT 与 MES 是仿真对端**，不是真系统；真车、真 RIoT 与现场 IO 不在本证据的主张范围内。
- 整体 `fullG3` 与 `releaseCandidate` 仍是 `INCONCLUSIVE`：本轮不含 RC。
- `FP-IS-00`／`06`／`14`／`15`、`FP-IS-04`／`05` 的 G3 面在同日另三个 runner：`../20260914-protocol-v1.0.0-staged-052759bc/`、`…-restart-052759bc/`、`…-demand-bearing-052759bc/`。
- 车载端这一半的 G2 在车载端仓 `evidence/g2/20260914-protocol-v1.0.0-8d19fee/`；服务端这一半在 `../../g2/20260914-protocol-v1.0.0-052759bc/`。
