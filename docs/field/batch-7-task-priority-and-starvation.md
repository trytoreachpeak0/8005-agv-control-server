# 批次 7：任务先后、防饥饿阈值与共晶机台的排除

批次7-09（control-server#214）。写给现场投运与运维：派车时任务按什么先后、防饥饿阈值没批之前系统怎么表现、
升级告警在哪里看、标定阈值的证据从哪里取，以及共晶、低温共晶机台靠什么排除在派车之外。

## 一、现场前提：共晶、低温共晶机台两处都不收录

**分区归属表不收录共晶、低温共晶机台，RIoT 地图上也不放它们的站点。**

REQ-0185 要求共晶与低温共晶在选任务阶段排除：需求照样留在积压里，但不派车、不装货。用户 2026-09-19 定：不另建一张
「排除区域」表，就靠分区归属表不收录它们来排除。服务端的判据链里有两处按分区归属表挡它（`AreaScopeCriterion` 与
`StationResolutionCriterion`，都读同一张表），与机台叫什么名字无关。

三种配置组合，批次7-09 整轮实测（`Batch7Req0185EutecticExclusionTests`）：

| 分区归属表 | RIoT 地图站点 | 结果 |
| --- | --- | --- |
| 不收录 | 有或没有都一样 | 需求留在积压，原因码 `OUT_OF_SCOPE_AREA`，不派车、不装货、**不告警**（不立结构性阻断，也不发防饥饿升级——它没有分区，也就没有阈值） |
| 收录 | 没有 | 原因码 `AREA_STATION_NOT_FOUND`，**立即形成结构性派车阻断告警**（按需求与原因去重，一直挂着） |
| 收录 | 有 | 照常派车 |

所以要记住的是第二行：**只从 RIoT 地图上删掉共晶机台的站点、分区归属表里还留着它，服务端会常年报
`AREA_STATION_NOT_FOUND` 结构性告警**，每一条共晶需求一条。要排除，先改分区归属表（FieldOps
`import-area-assignments` 导入一版不含它们的表）；地图站点删不删不影响排除本身，但为了两处一致，按上面的前提两处都不放。

## 二、任务按什么先后派

每一轮派车先把积压里的任务排出先后，再逐条为它找车（REQ-0200）。任务的先后按下面几层，前一层分不出高下才看下一层：

1. **超时层**：普通任务等待年龄达到本区防饥饿阈值，就进入超时层，排在所有没超时的任务前面——包括刚建的
   `STAGING_TO_WIRE`（REQ-0202）。超时任务之间仍按等待年龄排。
2. **优先级带**：`STAGING_TO_WIRE` 独占最高带；其余五类（`WIRE_TO_GATE`、`WIRE_TO_OPTICAL`、`WIRE_TO_NITROGEN`、
   `DIE_TO_WIRE_STAGING`、`DIE_TO_OVEN`）同在普通带，不再按类型细分。
3. **等待年龄**：等得久的先派。
4. 平手时按服务端第一次看到它的时刻，再按需求 id。

排序只决定「先问哪条任务」，**不绕过任何门禁**：超时的任务照样逐车走完整条判据链，缺箱数、站点被占、车不合格，
照样派不出去，车会给排在它后面、门禁全过的那条。

**等待年龄从哪里算。**从本地首次创建 TransportDemand 的时刻起算，也就是 MesIngest 目录项的 `CreatedAt`，
中间任何门禁、缺车、资源占用都不暂停、不重置（REQ-0201）。不用服务端「第一次看到」的时刻：那个时刻只在有车判过这条需求时
才写下，全车队都被挡住时、服务端停机时都不走，恰好违反「缺车不暂停」。MES 的 `DATES` 只用于展示和审计，不进排序。
MesIngest 与服务端是两台机器上的钟，两者相差几秒只影响年龄的零头；MesIngest 的钟快于服务端时，年龄按零算。

## 三、阈值没批之前：只计龄、不升级

**上线时每个分区的防饥饿阈值都是「未配置」**：取值要等「一次只开一个仓门」上线之后按实测数据标定、批准（人工票
control-server#219，用户 2026-09-21 定「等有了数据再确定」）。这段时间系统的表现是：

- 等待年龄照常累计，看板上照常显示；
- 不进超时层，所以不会越过 `STAGING_TO_WIRE`；
- 不发防饥饿升级告警。

阈值批了之后用 FieldOps 导入（control-server#216）：

```
ControlServer.FieldOps.exe import-dispatch-zone-parameters --input <csv>
```

CSV 的第三列 `starvation_threshold_seconds` 是秒，1～86400；留空就是未配置。**导入不用停车、不用重启服务端**：
正在进行的那一轮仍按开轮时读到的旧版本判，下一轮起按新版本。每次升级都记下它用的是哪一版参数。

## 四、升级告警

普通任务进入超时层的那一轮，服务端告警一次：

- **库里**：积压表 `JourneyBacklog` 这条需求那一行的 `StarvationEscalatedAt`（告警时刻）与
  `StarvationEscalationParameterVersion`（所用参数版本），两列在同一次保存里写下。看板的超时层展示读的是这两列
  （control-server#217）。
- **服务端日志**：Warning，事件 id 2161，文字以 `Starvation escalation:` 开头，写明需求、分区、已等秒数、阈值与参数版本。

**同一条需求只告警一次**：之后每一轮它都还在超时层，不再重报；服务端重启、需求离开 MES 目录又回来，也都不重报——
「已告警」记在库里，积压行在需求离开目录时只改原因码、不删。本轮就被接走的任务不告警。它不进
`blockingFacts`，车上看不到：它说的是「这条任务等太久了」，不是哪辆车出了故障。

一个已知的窄缝：告警标记提交之后、日志行写出之前服务端恰好崩掉，库里有告警、日志里少一行，下一轮不补日志。看板读的是库。

## 五、标定证据报表

阈值要按分区现场标定（REQ-0203）。服务端给一个只读查询，只给数，不套公式、不给建议值：

```
GET http://<服务端 HTTP 地址>/api/dispatch/starvation-calibration?from=<ISO 8601>&to=<ISO 8601>
GET http://<服务端 HTTP 地址>/api/dispatch/starvation-calibration?from=<ISO 8601>&to=<ISO 8601>&format=csv
```

第一个给 JSON，第二个给可下载的 CSV。`from`、`to` 必须都给、`from` 早于 `to`，否则回 400。样本期取 [from, to)，
所以**标定取样的窗要放在「一次只开一个仓门」（REQ-0357）上线之后**（规格第 20.2 节）。

每个分区一行，口径：

| 字段 | 意思 |
| --- | --- |
| `fullCycle` | 完整周期：一趟已完成的旅程从绑车到完成（车再次空闲、可接单），含空驶、运输与全部人工装卸。按旅程建立时刻落在窗里取样 |
| `waitToBind` | 建单至绑车等待：需求本地建单（MesIngest `CreatedAt`，与排序的等待年龄同一个起点）到它被绑进旅程。按绑车时刻取样 |
| `vehicleCount`、`taskCount` | 样本期里被绑车的需求条数，以及承载它们的车辆数 |
| `stationOperations` | 人工装卸：一次装（`LOAD`）或卸（`UNLOAD`）从服务端下发到人工确认提交的耗时，按下发时刻取样；没提交的不计 |

每个时长都给样本数、中位数（偶数个时取中间两个的平均）、P95（最近秩：第 ⌈0.95·n⌉ 小）、最大值，单位秒。

示例（CSV，两个分区；数字来自 `Batch7StarvationCalibrationReportTests` 的样本）：

```
dispatch_zone,window_from,window_to,vehicle_count,task_count,full_cycle_count,full_cycle_median_s,full_cycle_p95_s,full_cycle_max_s,wait_to_bind_count,wait_to_bind_median_s,wait_to_bind_p95_s,wait_to_bind_max_s,load_count,load_median_s,load_p95_s,load_max_s,unload_count,unload_median_s,unload_p95_s,unload_max_s
MAP-25-WIRE_TO_GATE,2026-10-01T00:00:00.0000000+00:00,2026-10-01T01:00:00.0000000+00:00,3,6,5,1200,3000,3000,4,150,240,240,2,45,60,60,1,45,45,45
MAP-26-STAGING,2026-10-01T00:00:00.0000000+00:00,2026-10-01T01:00:00.0000000+00:00,1,1,1,500,500,500,1,30,30,30,0,,,,0,,,
```

同一份的 JSON：

```json
{
  "from": "2026-10-01T00:00:00+00:00",
  "to": "2026-10-01T01:00:00+00:00",
  "generatedAt": "…",
  "zones": [
    {
      "dispatchZone": "MAP-25-WIRE_TO_GATE",
      "fullCycle": { "count": 5, "medianSeconds": 1200, "p95Seconds": 3000, "maxSeconds": 3000 },
      "waitToBind": { "count": 4, "medianSeconds": 150, "p95Seconds": 240, "maxSeconds": 240 },
      "vehicleCount": 3,
      "taskCount": 6,
      "stationOperations": [
        { "operationType": "LOAD", "duration": { "count": 2, "medianSeconds": 45, "p95Seconds": 60, "maxSeconds": 60 } },
        { "operationType": "UNLOAD", "duration": { "count": 1, "medianSeconds": 45, "p95Seconds": 45, "maxSeconds": 45 } }
      ]
    }
  ]
}
```

它是只读的，和 `/api/runtime/*` 那几个查询一样不带凭据；能不能从厂区网访问，由服务端 HTTP 绑定的网卡决定。
进看板或 FieldOps 由那两处的票接线，这里只交付查询。
