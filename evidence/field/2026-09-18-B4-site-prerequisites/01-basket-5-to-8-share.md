# 记录一：MVP 生产库 5～8 花篮需求占比

票：`trytoreachpeak0/8005-agv-control-server#67` 第 1 项。

## 结论

**统计时间窗内，MVP 实际派过车的 188 条需求中，`ExpectedBasketCount` 为 5～8 的是 0 条，占比 0.0%。**
每条需求的花篮数只有 1、2、3 三种（47 / 131 / 10 条），没有一条超过 4 篮。按区域号分列，62 个区域号每一个都是 0 条。

所以对批次 4「分侧后每侧只有 4 个仓」这一改动：在这段时间的实际业务里，没有一条需求会因为装不进单侧而形成结构性派车阻断。
出口报告可以据此说明分侧不是倒退，同时要带上下面「口径与局限」里的三条限定。

## 授权

| 项 | 内容 |
| --- | --- |
| 授权人 | 用户本人（Zhengyu Shao），2026-09-18，在调度会话中给出 |
| 授权范围 | 只读连接 factory01 上的 MVP 生产库，做这一次统计；只执行 SELECT；不停、不改任何服务、安装目录与配置；不碰 `SQAGV`、不碰生产 MesIngest |
| 查询审核 | 查询语句与取数方式于 2026-09-18 先送调度会话（Coordinator brief 调度）过目，调度回复「可以执行」后才执行；调度追加的三点要求（按需求去重核对、写明非一致性备份、查完删除副本）已照做 |
| 执行者 | 执行会话「实现 cs#67」（AI agent） |

## 库在哪里、怎么取的数

在 factory01 上只做了只读动作：读 `C:\Program Files\8005 AGV\ControlServer\appsettings.json` 与
`appsettings.Production.json` 两个配置文件、列目录、查服务状态。服务 `8005 AGV ControlServer` 为 Running（LocalSystem），
生产配置里的连接串是：

```
Data Source=C:\ProgramData\8005\ControlServer\data\controlserver.db
```

引擎是 SQLite，WAL 模式（主库 111 MB，另有 `-wal` 4.4 MB、`-shm`）。

**factory01 上没有执行任何 SQL，也没有运行 sqlite3。**取数方式：

1. 2026-09-18 21:17:28～21:17:43（+08:00）用 `scp` 把 `controlserver.db`、`controlserver.db-wal`、`controlserver.db-shm`
   三个文件依次读回控制端临时目录。这是**在线复制，不是一致性备份**：三个文件先后分别复制，不保证是同一时刻的快照，只用于统计。
2. 在控制端副本上用 Python `sqlite3`（SQLite 3.50.4）以 `mode=ro` 只读打开，先跑 `PRAGMA quick_check`，结果 `ok`，副本接受。
3. 2026-09-18 21:17:58（+08:00）执行下面的查询，原始输出见 [`basket-share/query-output.txt`](basket-share/query-output.txt)，
   执行脚本见 [`basket-share/run_queries.py`](basket-share/run_queries.py)。
4. 2026-09-18 21:18:06（+08:00）删除控制端的三个副本文件。删除前的 SHA-256：

   | 文件 | SHA-256 |
   | --- | --- |
   | `controlserver.db` | `f2fe97b3c90089a2e59a71a74e4da47a5291fd6326278056d5f00514c3c884a0` |
   | `controlserver.db-wal` | `84dabffc7d37138aef9442b1f6f8639400c1cfa2c6179cbeaeaae98197924167` |
   | `controlserver.db-shm` | `154b2fe1b8c903e6f9b6e816da33ec081eebbf45a611652df12f3fc540603be6` |

## 时间窗

库里能拿到的全部：以需求的 `AcceptedDemands.AcceptedAt` 计，
**2026-09-08 10:42:46 至 2026-09-18 21:05:54（+08:00）**，约 10 天半。库里存的是 UTC（`02:42:46` 与 `13:05:54`）。

## 口径与局限

1. **分母是「MVP 实际派过车的需求」，不是全部 MES 需求。**MVP 库里 `ExpectedBasketCount` 只存在 `JourneyDemands` 表：
   派车时按「子批箱数 ÷ 每篮容量，向上取整」算出并落库。没派上车的需求不落这个数，库里查不到它们的花篮数。
2. **只有 N 开头区域号，T 区在 MVP 里没有数据。**MVP 代码只接 N 开头区域（其余记为 `OUT_OF_SCOPE_AREA`），
   所以装片区（T 开头）的花篮数分布这里给不出。T 区投运前如需同样的判断，要另找数据来源。
3. **超过 8 篮的另见 Q4。**MVP 对算出 <1 或 >8 篮的需求记 `EXPECTED_BASKET_COUNT_OUT_OF_RANGE`，不派车。
   `JourneyBacklog` 里这个原因码为 0 条，箱数查不到（`SUBLOT_BOX_COUNT_UNAVAILABLE`）也是 0 条。
   另有 33 条当前原因码是 `SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE`（车上空仓暂时不够），这是等车空出来的排队状态，
   不说明它们的花篮数，也不计入上面的分母。

4. **样本只有 10 天半、188 条，不能外推。**库里最早的一条需求是 2026-09-08，这也是库里能拿到的全部
   （库里没有更早的需求，可能是那次部署时新建的库，这一点没有另外核实）。所以结论只能是「这段时间内没有出现
   5～8 篮的需求」，不能说成「永远不会有」。
5. **分侧后真出现这类需求会怎样。**分侧后每侧只有 4 个仓，5～8 篮的需求没有车能整单装下，会进入结构性派车阻断：
   系统立即告警，这条需求转人工送。它不会被拆开分两侧装，也不会一直静默排队。

`JourneyDemands` 与需求是否一对一已核对：188 行、188 个不同的 `DemandId`，没有一条需求出现多行，
也没有同一需求花篮数不一致的情况（QD、QD2）。查询里仍按 `DemandId` 分组去重，结果与不去重相同。

## 查询语句

全部是 SELECT（外加副本上的 `PRAGMA quick_check`）。子查询 `j` 按 `DemandId` 去重。

```sql
-- QC 副本完整性
PRAGMA quick_check;

-- Q0 表结构核对
SELECT name, sql FROM sqlite_master WHERE type='table' AND name IN ('JourneyDemands','AcceptedDemands');

-- QD / QD2 JourneyDemands 与需求是否一对一
SELECT COUNT(*) AS rows_, COUNT(DISTINCT DemandId) AS distinct_demands FROM JourneyDemands;
SELECT COUNT(*) AS demands_with_differing_counts FROM (
  SELECT DemandId FROM JourneyDemands GROUP BY DemandId HAVING COUNT(DISTINCT ExpectedBasketCount) > 1);

-- Q1 时间窗
SELECT COUNT(*) AS journeyed_demands, MIN(a.AcceptedAt) AS window_start, MAX(a.AcceptedAt) AS window_end
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId) AS j
JOIN AcceptedDemands AS a ON a.DemandId = j.DemandId;

-- Q2 按区域号分列（Q2T 为同一口径的合计）
SELECT json_extract(a.LiveMesFieldsJson, '$.Area') AS area, COUNT(*) AS total,
       SUM(CASE WHEN j.ExpectedBasketCount BETWEEN 5 AND 8 THEN 1 ELSE 0 END) AS basket_5_to_8,
       ROUND(100.0 * SUM(CASE WHEN j.ExpectedBasketCount BETWEEN 5 AND 8 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_5_to_8
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId) AS j
JOIN AcceptedDemands AS a ON a.DemandId = j.DemandId
GROUP BY area ORDER BY area;

-- Q3 花篮数分布
SELECT ExpectedBasketCount AS baskets, COUNT(*) AS demands
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId)
GROUP BY ExpectedBasketCount ORDER BY ExpectedBasketCount;

-- Q4 未能派车且与花篮数有关的原因码
SELECT ReasonCode, COUNT(*) AS demands, MIN(FirstSeenAt) AS first_seen, MAX(LastSeenAt) AS last_seen
FROM JourneyBacklog
WHERE ReasonCode IN ('EXPECTED_BASKET_COUNT_OUT_OF_RANGE','SUBLOT_BOX_COUNT_UNAVAILABLE','SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE')
GROUP BY ReasonCode;
```

## 结果

合计（Q2T）：188 条，5～8 篮 0 条，0.0%。

花篮数分布（Q3）：

| 花篮数 | 需求条数 |
| --- | --- |
| 1 | 47 |
| 2 | 131 |
| 3 | 10 |

按区域号分列（Q2）：62 个区域号，全部为 N 开头，每个区域号的 5～8 篮条数都是 0。逐行数字见
[`basket-share/query-output.txt`](basket-share/query-output.txt) 的 `== Q2` 段。条数最多的几个是
`N15-3`（15）、`N13-8`（11）、`N16-3`（7）、`N18-8`（7）。
