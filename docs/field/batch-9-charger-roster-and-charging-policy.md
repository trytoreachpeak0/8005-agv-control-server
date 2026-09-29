# 批次 9：充电桩名册（开窗与关窗）与充电策略版本

批次9-02（control-server#400，REQ-0171、REQ-0281、REQ-0282、REQ-0288，规格 5.5、8.6）。写给现场部署与运维：升级到含本票的版本之前
要先做什么、充电窗口怎么开怎么关、导入输出怎么读、充电策略怎么导入、批准、激活与回退。

本说明只讲操作，不去任何现场机器执行；真正的导入由批次9-13 按本说明做，每一次开窗都要用户在对话里单独授权。

## 一、合入之后的部署变化：先有已批准、已激活的策略，车才投运

**从这个版本起，一辆车没有「已批准、已激活、适用范围覆盖它」的充电策略版本时，不承接任何新用途**（规格 8.6：逐车硬阻断）。
服务端照常启动、照常连车，只是派车链对这辆车一律回 `CHARGING_POLICY_NOT_APPROVED`，服务端日志每轮每车一条：

```
Vehicle <VehicleKey> takes no new work: CHARGING_POLICY_NOT_APPROVED (no approved, activated policy version covers this vehicle).
Import, approve and activate a charging policy that covers it (REQ-0282).
```

服务端出厂**不带任何策略版本**，代码里也没有「没导入就用某个数」的回退。所以：

> **升级并行期实例（`scripts/parallel/` 装的那一套）到含本票的版本之前，先按第四节导入、批准并激活策略。**
> 否则升级后 agv02／agv03 一律不接活。这件事由批次9-13 在 10-07 前做。

名册不一样：名册为空（或一版都没导入）**不阻断**，服务端照常派搬运，只是不会自动充电，退化行为（人工充电等待并告警）在批次9-06。

## 二、充电窗口：开窗导入 211 名册，关窗导入空名册

用户 2026-09-29 定：v2 只在用户授权的窗口里自动充电。站 211「充电点1」在 25 号图（MVP、agv01）和 26 号图（v2）上是同一个物理桩，
RIoT 按图管占用、看不见另一张图上的车，所以**窗口外 v2 的名册保持为空**，v2 根本不会去分配 211。

置空与启用是**同一个动词、两份文件**，每一次都是一次受治理的版本导入（有预演、有 diff、写审计），不改库：

| 文件 | 用途 |
| --- | --- |
| `docs/field/charger-roster-map26-station211.json` | 开窗：登记 26 号图站 211，进出点 212，全部车辆可用 |
| `docs/field/charger-roster-empty.json` | 关窗：空名册 |

名册里桩的身份只来自这两份文件：RIoT 没有充电站类型（26 号图全是 `type=1`），导入校验不按站点类型或站名认桩。

### 开窗

1. **先要用户在对话里授权本次窗口**，并由现场确认 agv01 此刻不在充、也不会去充（MVP 那一侧不改配置）。
2. 预演：

   ```
   ControlServer.FieldOps.exe import-charger-roster --database "<服务端的库文件>" --input docs/field/charger-roster-map26-station211.json --catalog <stations-26.json> --map 26 --fleet "<agv02 的 VehicleKey>;<agv03 的 VehicleKey>" --dry-run
   ```

   `--catalog` 是 26 号图的站点目录（`{mapId, stations[{stationId, stationName}]}`，与等待点导入同一种文件）。输出里 `changes` 应是
   `211 ADDED`，`errorCount` 是 0。
3. 去掉 `--dry-run` 再跑一次：`outcome` 是 `OK`，`version` 是新版本号。服务端不用重启，下一次分配就按新名册判。

### 关窗

```
ControlServer.FieldOps.exe import-charger-roster --database "<服务端的库文件>" --input docs/field/charger-roster-empty.json --catalog <stations-26.json> --map 26 --fleet "<agv02 的 VehicleKey>;<agv03 的 VehicleKey>"
```

读输出里的三处：

- `emptyRoster: true`、`changes` 是 `211 REMOVED`：新的分配从这一刻起不会再用 211。
- `inProgress.openCycles`：**此刻还在进行的充电周期**（车、桩、线上状态、分配时刻、它记下的名册与策略版本），以及
  `inProgress.chargerReservations`：桩上的预占。它们**按原快照继续到结束**，本次导入不取消订单、不释放预占、不结束周期。
- `windowCanClose`：`true` 表示没有进行中的充电，窗口可以关；**`false` 表示窗口还不能关**——名册已经置空、只挡新的分配，但列出来的那辆车还在
  211 上或正开过去。等它充完离桩（再跑一次只读的 `charger-roster` 看 `inProgress` 变空），再让 agv01 用这个桩。

### 常见情况

- **重复导入同一份文件**：输出 `UNCHANGED`，不产生新版本，但**仍记一条审计**（`CHARGER_ROSTER_IMPORT_UNCHANGED`）——「关窗时名册本来就是空的」
  也是一次有人做过的关窗。
- **第一次导入就是空名册**（一版都没有时关窗）：写成版本 1。从此名册是「明确置空」，不是「从没登记」。
- **被拒**（`REJECTED`，退出码 1）：整份拒绝、一行不写，`errors` 一次列全，每类一个原因码：图号与 `--map` 不符、站不在目录里、站名与目录不一致、
  同一站写了两次、站是等待点、站是任务类型固定站、站是机台站、候选车辆里有投运名册之外的车、文件格式不对。
- **查看当前名册**（只读，库以只读模式开）：`ControlServer.FieldOps.exe charger-roster --database "<库文件>" [--version <n>]`。

## 三、导入不停车

名册导入与策略导入、批准、激活都不要求先禁用车辆，也不要求停服务端。已经在进行的充电周期、预占、订单继续用它们开始时记下的版本；新版本只作用于新的派车判断与新的充电周期。

## 四、充电策略版本：导入、批准、激活是三个留痕的动作

`docs/field/charging-policy-map26.json` 是投运参数（批次 9 方案第七节第 6 条的推荐值，用户 2026-09-29 同意）：

| 字段 | 值 |
| --- | --- |
| `chargingCompletionThresholdPercent`（充满线） | 80 |
| `mandatoryChargeEntryThresholdPercent`（强制充电线） | 30 |
| `minimumPostTaskBatteryMarginPercent`（最低任务后电量余量） | 20 |
| `estimatedTaskConsumptionPercent`（每趟任务耗电估计） | 10 |
| `progressStabilizationSeconds`（无进展判断的稳定期） | 180 |
| `progressObservationWindowSeconds`（观察窗口） | 600 |
| `progressMinimumIncreasePercent`（最小增量） | 3 |
| `vehicleScope` | 空，即全部投运车辆 |

导入时校验三个阈值的硬关系：充满线 > 强制充电线 ≥ 余量（`REQ-0281`，这里是 80 > 30 ≥ 20）。字段一个都不能缺，没有缺省值。

1. **导入**（只写版本，不批准、不激活）：

   ```
   ControlServer.FieldOps.exe import-charging-policy --database "<库文件>" --input docs/field/charging-policy-map26.json --fleet "<agv02 的 VehicleKey>;<agv03 的 VehicleKey>" [--dry-run]
   ```

   输出的 `impact` 列出：相对当前生效版本改了哪些值（`changes`）、生效后覆盖哪些车（`coveredAfter`）、**生效后失去策略的车**（`vehiclesLosingPolicy`）
   与生效后仍没有策略的车（`vehiclesWithoutPolicyAfter`），以及正在按旧快照进行、不受影响的充电周期（`openCycles`）。
2. **批准**（记批准人、角色、依据引用与来源）：

   ```
   ControlServer.FieldOps.exe approve-charging-policy --database "<库文件>" --version <n> --approved-by "<批准人>" --role <角色> --basis "<依据引用>" --source FIELD
   ```

   现场批准的来源是 `FIELD`。`TEST_FIXTURE`、`L2_PRESET` 只给测试夹具与 L2 用。
3. **激活**（只收已批准的版本）：

   ```
   ControlServer.FieldOps.exe activate-charging-policy --database "<库文件>" --version <n> --activated-by "<操作人>" --fleet "<agv02 的 VehicleKey>;<agv03 的 VehicleKey>" [--dry-run]
   ```

   一个只有测试批准的版本，不带 `--allow-non-field-approval` 会被拒（`CHARGING_POLICY_ONLY_NON_FIELD_APPROVAL`）。**现场不要带这个开关。**
4. **核对**：`ControlServer.FieldOps.exe charging-policy --database "<库文件>" --fleet "<...>"`（只读）列出当前生效版本、全部版本的批准与激活历史，
   以及每辆车投不投运。

**策略按「全局一个生效版本」存**：激活一版只覆盖部分车的策略，范围外的车会立刻不接活。激活前看 `impact.vehiclesLosingPolicy`，不是空的就先想清楚。

`--role` 按写的记，不做人员认证。

### 回退

没有「拨回指针」：把旧内容再导入成一个新版本、批准、激活。内容与最新一版逐字相同时导入报 `UNCHANGED`、不写新版本——这时直接对那个旧版本号
（已批准过的）再跑一次 `activate-charging-policy` 即可，激活只追加一条记录。

### 10-08 的临时测试策略

方案第七节第 6 条提到 10-08 演练用的临时策略（强制充电线 50、充满线 62）。它是一版独立的策略文件，同样走导入、批准、激活三步；演练完照上面「回退」
换回投运参数那一版。本票不带那份文件。
