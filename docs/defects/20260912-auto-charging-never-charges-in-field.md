# 缺陷：自动充电在现场从来做不成——充电桩绑定对不上地图、起不了充电照常接单、充电单只有移动动作

Status: fixed（`3c9ced4`，未上线，现场验证归 8005-agv-program#20 的充电短窗口）
Tracking: [8005-agv-program#53](https://github.com/trytoreachpeak0/8005-agv-program/issues/53)
Found by: 现场窗口二（无人）续跑，`agv01`，2026-09-12，证据 `evidence/field/20260912-FW-FL2-resumed/`（窗口没有 finalize，见同目录 `ABORTED.md`）。
Product at discovery: 服务端 `e0d6df7`、车载端 `6b8a0b0`、`protocol-v0.3.0`（run `34678572182` 的包）

---

## 现象

窗口二的充电那一幕（FL2-CH）先后派了两次车，一次都没有充上电：

| 时刻（本地） | 事件 |
| --- | --- |
| 15:38:01 | `Set-JourneyRuntime.ps1 -Dispatch`，临时充电线 76 / 80。车 75%、`NO_CHARGE`、`IDLE`，没有在途旅程 |
| 15:38:04 | `[2107] Charger station could not be resolved on the current map ... Map 25 does not contain exact fixed station 充电准备点1/211.` |
| 15:38:06 | 引擎受理了新旅程 `afc0ce0a`（5 个取货站），车开往产线；**没有写任何 `AutoChargingRuns` 行** |
| 16:16 | 用户在 RIoT 把 211 改名为「充电点1」（改名前是「站点211」；212 是「充电准备点1」），生产配置跟着加了 `chargerStationId=充电点1` |
| 16:20:43 | 第二次派车，临时充电线 68 / 72。充电行程 `bc7244b5` 在 67% 触发，`TO_CHARGER` 单 `W2G-CHARGE-bc7244b5-…-1` 去 211，RIoT 确认 |
| 16:23:46 | 行程进入 `Charging`（车已到 211） |
| 16:24:06 起 | `CHARGER_NOT_ENGAGED`，RIoT 一直是 `batteryState=NO_CHARGE`。用户在现场确认：**订单里只有移动动作，没有充电动作** |

## 根因

三层，各自独立，任何一层单独都会让自动充电在现场做不成。

**第一层：出厂的充电桩绑定从没和现场地图核对过。**`src/ControlServer.Host/appsettings.json:60-61` 写的是
`chargerStationId=充电准备点1`、`chargerStationRiotId=211`。`MapStationResolver.RequireFixedStation` 要求站号与站名在
`GET /api/imap/v1/mapInfo/stations/25` 里**同时**精确匹配。现场 211 当时叫「站点211」，「充电准备点1」是 212 的名字。
L2 的 `auto-charge-endurance` 与 `real-onboard-field-window2-rehearsal` 都往假 RIoT 地图里注入了 `211=充电准备点1`，
所以这组值只和测试自己造的地图对得上。

**第二层：充电起不来时，引擎照常接单（fail open）。**`JourneyRuntimeEngine.cs:157-161`：`AdvanceAutoChargingAsync`
返回 `false`，同一轮就进 `DiscoverAndAcceptAsync`。`TryStartAutoChargingAsync` 在充电桩解析失败时只记一条 Warning
然后返回 `false`（`:1890-1900`）。而接单用的 `ValidateDynamicFacts` 只看 `minimumBatteryPercent`（`:922`），
根本不知道还有一条充电触发线。所以「该充电了但没法充」和「不用充电」在接单这一侧是同一个结果。
出厂阈值下这个缺口不容易看到：触发线 20 低于最低电量 30，车在 30 以下先被接单拒掉。但它照样会停在 20–30 之间，
既不接单也不去充电。

**第三层：充电单只是一张移动单。**`src/ControlServer.Infrastructure/Adapters/HttpRiotMovementGateway.cs:93`
的 `CreateAsync` 对所有 `OrderIntent` 一律调用 `riotSession.Order.CreateMoveOrderAsync`，不看 `Purpose`。
`TO_CHARGER` 于是和去取货站一样，只让车开到 211，RIoT 不会下发充电动作，桩不通电。
这也解释了 2026-09-11 到 12 日车在 211 停了 13 小时一直 `NO_CHARGE`：停在桩上本身不会充电。
L2 的假 RIoT 在车到桩时直接把 `batteryState` 写成 `CHARGING`，所以测不出这一层。

## 修复（`3c9ced4`，2026-09-12）

1. **充电单**：`HttpRiotMovementGateway` 对 `TO_CHARGER` 下 `move(桩) + act(78,1,0)`，经 facade 的 Kiota 客户端建单，
   沿用 facade 的应答检查，没有切新 SDK 包。没有用 `/api/task/v1/order/charge/{vehicleKey}`：program 仓票 04 的 Q11
   早已否决它（RIoT 本体充电机制、要求站点设成充电桩 type），`CONTEXT.md` 的 `RoutineOrderCreationCall` 定的就是
   `move + act(78,1)`。
   生产 RIoT map 25 实读（只读 GET）：208 个站全是 `type=1`，只有 211 配了 `user_define_properties.enter_exit = "212"`，
   所以 RIoT 会把充电单展开成 `move(212) → move(211) → act(78)`。对账原来要求恰好一段移动，现改为取最后一段，
   多段时最后一段必须与 `endStationNo` 一致。
2. **fail closed**：开着自动充电时，电量低于触发线拒绝接单（`BATTERY_CHARGE_REQUIRED`）；桩在当前地图上解析不到时
   不论电量一律拒绝接单（`CHARGER_STATION_UNRESOLVED`，用户定），绑定错误在第一次派车就暴露。出厂值改为 `充电点1 / 211`。
3. **空档**：配置校验改为要求触发线不低于接单最低线（用户定），出厂触发线 20 → 30。
   `Set-JourneyRuntime.ps1` 在写配置前按同一规则拒绝（工作区 `6dd66d4`）。
4. **充电器接不上**：Q-033 说充电单重试后 `orderState=9` HANG，现在充电行程写 `CHARGER_ORDER_HANG`，不再静默等待。
5. **L2**：假 RIoT 只在带 `act(78,1,0)` 的单推到 5 时报 `CHARGING`，两条充电场景不再在到桩时自己写
   （`L2-AC-19`、`L2-FW2-19`）；默认种子地图加上 `211 充电点1`。

验证：单元测试 446 全绿；L2 `20260912-normal-load-001` PASS、`20260912-auto-charge-endurance-001` PASS、
`20260912-real-onboard-field-window2-rehearsal-005` PASS/41。红证据 `-004`：彩排 setup 的假地图仍写旧名「充电准备点1」，
四条需求全是 `CHARGER_STATION_UNRESOLVED`（`b31b6d8` 修）——新规则按设计把绑定错误挡在了第一次派车。

**没有验证的**：生产 RIoT 上 78 号动作模板是否存在、agv01 执行 `act(78,1,0)` 能否真的接上电。Round 24/25 在测试环境
`172.10.1.72` 上测，只有现场短窗口能回答。

## 修复方向（原记录，已按上节实施）

1. 出厂值改成现场值 `充电点1 / 211`（生产上已用 `appsettings.Production.json` 覆盖）。另外，让固定站点绑定在现场能被尽早发现：
   比如服务起来时就对 RIoT 当前地图解析一次，对不上就不报 ready。只靠「该充电时才报一条 Warning」是不够的。
2. 充电触发线要进入接单判断：`autoChargingEnabled` 且电量低于触发线时拒绝接单，不管充电行程这一轮能不能起来；
   起不来的原因要写进 backlog 原因，不能只进日志。顺带把出厂 20 / 30 的空档理清。
3. `TO_CHARGER` 要下带充电动作的 RIoT 单。用哪个接口，要先在 `riot-sdk` 与 RIoT 规范里确认（`task.json` 里有
   `/api/task/v1/order/charge/{vehicleKey}`，是否就是它、有无副作用，未验）。L2 假 RIoT 要改成只在收到充电动作时才报 `CHARGING`。

## 现场遗留

- 充电行程 `bc7244b5` 停在 `Charging / CHARGER_NOT_ENGAGED`，库里没有结束它的产品路径。
  服务端下次开门会一直卡在这条行程上：没在充电，电量又低于恢复线，所以不释放、不接单。
  开窗前要先结算它（与 `closing-stuck-production-journey` 同一类处境）。
- factory01 `appsettings.Production.json` 保留 `JourneyRuntime.chargerStationId=充电点1`。
