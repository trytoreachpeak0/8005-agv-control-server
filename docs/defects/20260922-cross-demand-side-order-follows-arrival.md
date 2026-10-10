# 缺陷：同一停靠多条需求卸货不按侧排，按加入先后（规格缺口，cs#303）

Status: 调度 2026-09-22 判为规格缺口，修复票 trytoreachpeak0/8005-agv-control-server#303（挡批次 7 出口）：卸货按侧下发、前侧先于后侧；装货跨需求的先后仍由操作员扫码顺序决定，服务端不改。本票不改产品代码
Owner repository: `8005-agv-control-server`（`JourneyStopCursor.NextToUnloadAtCurrentStop`、`JourneyRuntimeEngine` 的取货逐条串行；批次7-06，control-server#211）
Found by: control-server#218 开工实读 `fp/v2-impl@8ee99549`，写 `real-onboard-mixed-side-one-stop` 时（`scripts/l2/scenarios/real-onboard-mixed-side-one-stop.ps1` 头注释）
Product at discovery: control-server `8ee99549`

## 规格怎么说

需求基线 v1.4.0 `REQ-0357`：业务仓位操作一次只开一扇仓门；顺序是「同一指令的目标集合内先前侧（`FRONT`）后后侧（`REAR`），组内按仓位号
从小到大」。规格第 20 节补记（`full-product-scope-and-sequence-v2.md` 第 1392 行一带）把第 5.1 节第 4 条「为各需求分别开各自那一组」
补成「两组仓门同样一次一扇、先前侧后后侧」。control-server#218 票面据此要求真装置场景断言「前侧需求先于后侧需求（跨需求的先后是服务端发命令的顺序）」。

## 代码今天怎么做

- **卸货**：一次只下一条需求的卸货命令，挑的是本停靠第一条 `LOADED` 的需求，按归属加入旅程的先后 `AddedAt`、再按 `DemandId`
  （`JourneyStopCursor.cs` 的排序，约 `:347-350`；`NextToUnloadAtCurrentStop` 约 `:140`）。没有按侧排。
- **装货**：到站发一条录入请求列出本站全部未装的子批，操作员扫哪一条就装哪一条（`RevalidateEnteredSublotAsync`），装完一条才发下一条的
  录入请求。先后是扫码先后，服务端不按侧挑，也不拒绝先扫后侧。
- `src/` 里没有跨需求按侧排序的代码；`ADR-cross-0061` 在 `src/` 里搜不到。REQ-0357 的实现在车载端（onboard-hmi#106：同一指令内一次一扇）。

## 所以

一次停靠上前后两侧各一条需求时，「先前后后」今天成立与否取决于**后侧需求是不是后加入的**（卸货），以及**操作员是不是先扫前侧**（装货）。
后侧需求先追加进旅程时，关卡上会先开后侧那一扇。一次一扇仍然成立，被违反的只可能是跨需求的先后。

`real-onboard-mixed-side-one-stop` 让前侧需求（乙）先于后侧需求（丙）追加、先扫乙，所以它的 `L2-MSO-04`、`L2-MSO-10` 今天是绿的；
**这两条的绿不能读成「服务端按侧排序」**。它们的红证据用的是本地缺陷提交（把卸货的排序倒过来），证的是判据能分辨先后，不是产品有按侧的规则。

## 处置

调度判定（2026-09-22）：规格第 20 节的「先前侧后后侧」对**卸货**也约束同一停靠内的跨需求先后，今天的实现是缺口，由 control-server#303 修。
**装货**跨需求的先后由操作员扫码顺序决定，服务端不改。

`real-onboard-mixed-side-one-stop` 在 control-server#303 合入之前让前侧需求先追加，所以 `L2-MSO-10` 的绿不代表服务端有按侧的保证。
control-server#303 合入后，场景要改成**后侧需求先追加**，「先前后后」才由保证撑住：control-server#218 转 ready 时若 #303 已合入就在本票改，
否则由出口票 control-server#220 接手。
