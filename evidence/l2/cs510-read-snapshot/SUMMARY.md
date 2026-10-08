# control-server#510：充电场景探针读到新旧混合状态（同一读快照）

Found by: CI l2 run 37595371819（cs#503 / PR #509 的那一轮，第 1 次尝试红 L2-UTC-01，第 2 次尝试红 L2-CWP-05）。

被测提交：`a0553a0a15d73e3487f0c9cdbc3cc96b6ec7589a`（本分支），本机合成装置，端口槽位 0，2026-10-08，调度放行的时段。

## 结论

- **产品没有中间态。** 暂停、周期迁移到 `UNABLE_TO_CHARGE`/`CLEARING`、用途、旅程码是同一次 `SaveChanges`
  （`JourneyRuntimeEngine.UnableToCharge.cs` `ConfirmUnableToChargeAsync`）；到等待点完成清桩是一个显式事务
  （`JourneyRuntimeEngine.ClearanceMove.cs` `CompleteClearanceAtWaitingPointAsync`）。本票不改产品代码。
- **红的原因在探针。** 探针先单独读充电周期，再用几条查询读其余各项，连接是自动提交，每条 `SELECT` 各读各的时刻；
  服务端在第一条和第二条之间提交，就得到库里从没有过的「周期是提交前、其余是提交后」，而等待条件只看后读的那段。
- **修法：** 新增 `Invoke-L2ReadSnapshot`（一个读事务，几条读看到同一个已提交状态），7 处探针改用它；能等整串的改为等整串。

## 注入证据

注入：只在第一次探针里，读完充电周期之后，另开一个只读连接轮询，直到服务端那次提交可见（UTC 看暂停行出现，CWP 看周期
`ENDED`），再接着读。这把 CI 碰巧撞上的时序变成每次必然。改动见 `variants/*.diff`，没有提交进分支，跑完用
`git checkout` 还原（`variants/run.ps1`）。每次都以 journal 里的 `CS510_INJECTION_FIRED` 为准：6 次注入全部触发。

| # | 场景 | 变体 | 注入 | 结果 | 判据 |
| --- | --- | --- | --- | --- | --- |
| 1 | charging-unable-to-charge-pauses-charger | 新（快照 + 等整串） | 无 | PASS | 6/6 |
| 2 | 同上 | 旧（基线 `a419a2a8` 的探针与等待） | FIRED | **FAIL** | L2-UTC-01 红，其余 5 条绿 |
| 3 | 同上 | B（旧探针，只把等待改成等整串） | FIRED | PASS | 6/6 |
| 4 | 同上 | 新 | FIRED | PASS | 6/6 |
| 5 | charging-clearance-to-waiting-point | 新 | 无 | PASS | 6/6 |
| 6 | 同上 | 旧 | FIRED | **FAIL** | L2-CWP-05 红，其余 5 条绿 |
| 7 | 同上 | B | FIRED | PASS | 6/6 |
| 8 | 同上 | 新 | FIRED | PASS | 6/6 |
| 9 | charging-full-cycle | 新 | 无 | PASS | 5/5 |

两次红的读数与 CI 那两次逐字同形：

- 2 号 L2-UTC-01 实际：`UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 | EN_ROUTE ACTIVE | CLEARING_MAINTENANCE … | CHARGING_UNABLE_TO_CHARGE`
- 6 号 L2-CWP-05 实际：`(none) | CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT | CLEARING  | Completed … | … | OCCUPIED …`

### 方案 B 在注入下也是绿的

如实记录：只把等待条件改成等整串（3、7 号），在这两条判据上就足以不红。但它读到了混合状态，只是没停在那一行——
`timeline.jsonl` 里注入那一轮的观测：

- 3 号（B）：`… | EN_ROUTE ACTIVE | CLEARING_MAINTENANCE … | CHARGING_UNABLE_TO_CHARGE`，下一轮才是一致的新状态。
- 4 号（新）：`(none) | EN_ROUTE ACTIVE | CHARGING … |`，即提交前的一致状态，下一轮是提交后的一致状态。
- 7 号（B）：`(none) | CHARGER_RELEASED_… | CLEARING  | Completed …`；8 号（新）：`RESERVED … |  | CLEARING  | AwaitingPickupArrival …`。

所以 B 不够的地方在于：等待条件不能写成等整串的探针。G3-13-02（`g3-automatic-charging-cycle`）的等待条件故意在
「任何不是两种进行中状态的值」上停下，用来抓错误迁移；那种写法下混合读数（`CHARGING | (none)`）会直接停下并变红，只有快照能防。

## 自检与变异（`self-check/`）

`Test-L2ReadSnapshot.ps1`，真实 SQLite（WAL），9 格全绿（`head.txt`）。三个变异都由具体格子杀死，不是脚本崩溃：

| 变异 | 红的格子 |
| --- | --- |
| M1 去掉事务 | 2：块内跨表同一状态；嵌套被拒 |
| M2 COMMIT 不在 finally | 4：抛错之后下一块读到新提交；之后回到自动提交；嵌套；嵌套之后 |
| M3 不 COMMIT | 6：下一块读到新提交，及其后全部 |

`g3-automatic-charging-cycle` 是真车载端的 G3 场景：本机没跑，也没申请真装置，只做了语法解析（0 错误）。G3 跑的是绑定提交里的场景脚本，
这处改动要等 G3 绑定前移之后才生效。
