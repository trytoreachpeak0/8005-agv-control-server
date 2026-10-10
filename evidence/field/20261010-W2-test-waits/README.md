# 等待时长临时调短（测试期临时值，进入生产状态前改回）

## 授权

- 用户原话（转达经 Coordinator11）：「类似离站等待和持货等待的时间太长了，我们这是测试，越快越好，等进入生产状态再变回原来的数值」
- 2026-10-10 20:06 本会话 AskUserQuestion「等待时长是否临时改短」用户选：「离站 1 分钟、持货 3 分钟 (Recommended)」
- 同时用户选段序：「B → F充电 → A2/C/E/D (Recommended)」

## 改动（V2 的 C:\Program Files\8005 AGV\ControlServer.V2ppsettings.Production.json，JourneyRuntime 节；来源是实例定义 journeyRuntime.*）

| 键 | 原值 | 测试期临时值 | 校验下限（JourneyRuntimeOptions.cs:302-320） |
| --- | --- | --- | --- |
| stationDepartureWaitTimeout | 00:05:00 | 00:01:00 | 0（关）或 ≥5 s |
| cargoHoldingTimeout | 00:30:00 | 00:03:00 | >0 |

离站等待缩短同时是在 cs#573（车辆安全投影间歇 COVERAGE_UNKNOWN 导致离站等待被重计）下的有意规避。

**改回办法**：把上表两个键改回原值并重启 V2；或下次按实例定义重装（定义里仍是原值）。

## 重装或回滚会悄悄改回

这两个值是直接改在服务器的 appsettings.Production.json 上的，而这份文件由 19-deploy-control-server-parallel.ps1 按实例定义 journeyRuntime.* 生成。今晚或以后只要用 19 重装或回滚，它们就会回到 00:05:00 和 00:30:00。重装后要么照上表重新改一次，要么用一份部署副本定义带上这两个值（只放 scratchpad 或证据目录，不要提交回仓库）。（Coordinator11 提醒，2026-10-10）

## 20:30 修订：离站等待 1 分钟 → 3 分钟

用户原话：「放宽到 3 分钟 (Recommended)」。原因：1 分钟同时是取货站录条码期限，B 段第一次因此 CANCELLED_BY_STATION_TIMEOUT。
