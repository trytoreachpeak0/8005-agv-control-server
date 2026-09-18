# 记录三：分区归属表（N 区）编制、批准与 dry-run 校验

票：`trytoreachpeak0/8005-agv-control-server#67` 第 3 项。

## 结论

**N 开头区域号（焊线区、键合区）的分区归属表已由用户批准，并在一份测试库上用
`ControlServer.FieldOps import-area-assignments --dry-run` 校验通过（`outcome: OK`，217 行，0 个错误）。**
T 开头区域号（装片区）的归属与开门侧还定不下来，**没有写入表**，下面单列为待定。

本票只做校验，**没有在任何库上正式导入**。正式导入在 v2 实例上的 W2／W3 窗口执行，不在本票。

## 表

文件：[`area-assignments/area-assignments-N-revised.csv`](area-assignments/area-assignments-N-revised.csv)，
SHA-256 `a9f1a0e18a938049f01c28627fa7a2327c60be1a0c0f5d3a4534ab8d2e5c0c4e`。无 BOM 的 UTF-8（纯 ASCII），LF 换行，
表头 `area,dispatch_zone,slot_position`。

| 项 | 值 |
| --- | --- |
| 行数 | 217 个区域号，即地图 26 上全部 N 开头区域号（见[记录二](02-station-split-check.md)），不多不少 |
| `dispatch_zone` | 全部 `WIRE` |
| `slot_position` | `REAR` 112 个、`FRONT` 105 个 |

编制过程：执行会话先按用户口径（全部 `WIRE`／`FRONT`）从地图 26 站点清单生成草稿；用户随后亲自按现场情况逐个区域号改了开门侧，
交回的就是上面这份修订版。草稿被修订版取代，没有保留。

与地图 26 站点的对照：两机台共用一个停靠位的 79 个站点，每站两台填的是同一侧；三机台共用的 9 个站点，第三台填了另一侧：

| 站点 | 三个区域号的开门侧 |
| --- | --- |
| `N2-16_N3-15_N3-16` | REAR／REAR／FRONT |
| `N4-16_N5-15_N5-16` | FRONT／FRONT／REAR |
| `N6-16_N7-15_N7-16` | REAR／REAR／FRONT |
| `N11-9_N12-8_N12-9` | FRONT／FRONT／REAR |
| `N13-8_N14-7_N14-8` | REAR／REAR／FRONT |
| `N15-8_N16-7_N16-8` | FRONT／FRONT／REAR |
| `N17-8_N18-7_N18-8` | REAR／REAR／FRONT |
| `N19-8_N20-7_N20-8` | FRONT／FRONT／REAR |
| `N21-8_N22-7_N22-8` | REAR／REAR／FRONT |

开门侧够不够得着机台架子由现场判断，系统发现不了（规格 5.1 第 4 条），填错会在首次真实作业时表现为开门后够不着、需人工处理。

## 批准

| 项 | 内容 |
| --- | --- |
| 批准人 | Zhengyu Shao（用户本人） |
| 日期 | 2026-09-18 |
| 批准对象 | 上面这份修订版 CSV（SHA-256 见上），在 dry-run 通过后于执行会话中批准 |

`dispatch_zone` 全部填 `WIRE`、N 区范围取地图 26 全部 N 区域号，都是用户 2026-09-18 在执行会话中定的；开门侧由用户逐个填写。

## T 区待定

按 `REQ-0191`，未写入表的区域号静默跳过：T 开头区域号的需求在这份表导入之后**不会被派车**，也不报错。
地图 26 上共有 120 个 T 开头区域号，逐个列在 [`area-assignments/t-areas-pending.csv`](area-assignments/t-areas-pending.csv)
（含所在站点名），状态 `PENDING`。按机台排汇总：

| 前缀 | 个数 | 范围 |
| --- | --- | --- |
| `T01` | 9 | `T01-01`～`T01-09` |
| `T02` | 9 | `T02-01`～`T02-09` |
| `T03` | 8 | `T03-01`～`T03-08` |
| `T04` | 8 | `T04-01`～`T04-08` |
| `T05` | 8 | `T05-01`～`T05-08` |
| `T06` | 8 | `T06-01`～`T06-08` |
| `T07` | 9 | `T07-01`～`T07-09` |
| `T08` | 9 | `T08-01`～`T08-09` |
| `T09` | 7 | `T09-01`～`T09-07` |
| `T10` | 1 | `T10-01` |
| `T11` | 10 | `T11-01`～`T11-10` |
| `T12` | 9 | `T12-01`～`T12-09` |
| `T13` | 9 | `T13-01`～`T13-09` |
| `T14` | 9 | `T14-01`～`T14-09` |
| `T15` | 7 | `T15-01`～`T15-07` |

T 区定下来后，做法是在这份 N 区表后面追加 T 区行、整份重新批准、重新 dry-run，写一份新的记录指向本记录。
MVP 库里没有 T 区的花篮数数据（见[记录一](01-basket-5-to-8-share.md)），T 区投运前如需判断单侧装不下的比例，要另找数据来源。

## dry-run 校验

### 测试库怎么来的

测试库是一份新建的空库，放在控制端临时目录，**不是任何生产库或 v2 实例的库**，校验完即弃。

1. 在本 worktree（`fp/v2-impl` 的 `63ddbbae`）Release 构建 `ControlServer.Host` 与 `ControlServer.FieldOps`。
2. 用 `ControlServer.Host` 以临时库启动一次，只为跑数据库迁移：`JourneyRuntime` 关、`OnboardTransport` 关、端口改为 58905／58907，
   健康检查有响应后立即停止并核对没有残留进程。
3. `ControlServer.FieldOps seed-approved-facts`：灌入已批准的八仓整车模型，已发布分组为 `FRONT`、`REAR`。输出：
   `{"command":"seed-approved-facts","outcome":"OK","modelKey":"8005-eight-slot","slotModelVersionId":"692ecace5b9f4149bc71ab2c5e5d41b4","version":1,"slotCount":8}`
4. **在测试库里手写一行调度策略**：`DispatchZoneVehicles`（`Zone=WIRE`、`AgvId=agv02`、`ConfigurationVersion=cs67-test-db`）。
   导入工具判「分区存在」读的是这张表，正常由服务端在 `JourneyRuntime` 运行时按配置写入。起一个带 `JourneyRuntime` 的服务端要接
   RIoT 与 MES，代价与风险都与这次校验不相称，所以这里直接写入，模拟「v2 实例已按 `dispatchZone=WIRE` 起过一次」。
   这一步证明的是**表本身**合规；它不证明 v2 实例的配置已经是 `WIRE`，那是下面投运前置里的检查项。

测试库的真装置时段由调度会话放行（2026-09-18），用完已归还。

### 命令与输出

2026-09-18 22:00:27（+08:00）执行，退出码 0：

```
ControlServer.FieldOps.exe import-area-assignments --database <临时目录>\controlserver-test.db --input evidence\field\2026-09-18-B4-site-prerequisites\area-assignments\area-assignments-N-revised.csv --dry-run
```

```json
{"command":"import-area-assignments","outcome":"OK","dryRun":true,"input":"C:\\Users\\szy\\Desktop\\8005-workspace-v2\\worktrees\\b4-03-8005-agv-control-server\\evidence\\field\\2026-09-18-B4-site-prerequisites\\area-assignments\\area-assignments-N-revised.csv","entryCount":217,"version":null,"contentSha256":null,"snapshotId":null,"importedAt":null,"errorCount":0,"errors":[],"preview":[]}
```

原样另存于 [`area-assignments/dry-run-output.json`](area-assignments/dry-run-output.json)。`version`、`snapshotId` 为空，
`preview` 为空（测试库里没有在途需求）；执行后查 `DispatchZoneAreaAssignmentVersions` 仍为 0 行，确认 dry-run 没有写库。

## 投运前置（切生产时的检查项）

1. **v2 实例的 `JourneyRuntime.dispatchZone` 必须配成 `WIRE`**（`allowedDispatchZones` 同样包含 `WIRE`），并以这份配置
   起过一次，这张表才导得进去。v2 默认配置目前是 `MAP-25-WIRE_TO_GATE`，照原样导入会整份拒绝（`DISPATCH_ZONE_NOT_FOUND`）。
   配置里的 `mapId` 也要是 26，不是 25。
2. **N 区 217 个区域号的开门侧是用户 2026-09-18 定的**：`FRONT` 表示只用前侧 1～4 号仓，`REAR` 表示只用后侧 5～8 号仓。
   每条 N 区需求只会被装进它所属一侧的 4 个仓里。
3. **T 区 120 个区域号待定，未写入表**，导入后 T 区需求静默跳过、不派车（`REQ-0191`）。T 区要投运，先补表。
4. 正式导入用同一个命令去掉 `--dry-run`，在 v2 实例的库上执行；导入会形成新的不可变版本并留快照与审计。
