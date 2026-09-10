# ControlServer.FakeOnboard

合成车载端：连到 ControlServer，走完 ADR-cross-0029 的五步恢复握手，之后持续心跳，并按一份可以
在会话存活期间随时改的策略应答服务端的业务请求。

```bash
CONTROL_SERVER_ONBOARD_CREDENTIAL=<凭据> \
dotnet run --project tools/ControlServer.FakeOnboard -- --FakeOnboard:Peer:port=58005
```

控制面默认监听 `127.0.0.1:58009`，非 loopback 默认拒绝启动。协议流量是另一条到 ControlServer 的
TCP 连接，不走 HTTP。

握手那条 `SafetyStateSnapshot` 携带的安全摘要可以在启动时给定：

```bash
dotnet run --project tools/ControlServer.FakeOnboard --   --FakeOnboard:Peer:port=58005   --FakeOnboard:Seed:vehicleStopped=false
```

六个键对应 `SafetySummary` 的六个字段：`departureSafe`、`vehicleStopped`、`allTargetSlotsLocked`、
`allUnlockOutputsReset`、`unknownPresent`、`reasonCodes`（数组写成 `reasonCodes:0=...`）。
**这是唯一能让会话在「车还在动」的状态下建立的入口**——`PUT /control/v1/safety` 只能报告一个
已经存在的会话的变化，而 2026-09-03 现场那个缺陷的形状恰恰是会话建立那一刻快照就已经记着
`vehicleStopped=false`。

给了 `--FakeOnboard:SchemaRecordPath=<文件>` 时，它每发一行协议报文就往那个文件追加一条
`{"messageType","origin":"synthetic-peer","site","line"}`，L2 编排器收尾时交给
`tools/ControlServer.SchemaConformance` 逐条对 protocol JSON Schema 校验。**合成对端的报文只在
L2 里被校验**：`tests/ControlServer.Tests` 不引用这个项目，`dotnet test` 与 G2 一条它的报文都不会
产生。替身发错报文的代价是服务端对着一份假契约开发、而门禁照样全绿（`8005-agv-program#33`/`#35`）。

**它不是真车载端，也永远不会是。**没有 IO、没有 journal、没有操作员。它有的是服务端状态机所依赖
的那部分协议行为——这正好够让一个场景真正关于服务端。真车载端要等落地顺序第 5 步的 UIA 驱动。

这个工具原本只做「握手完就退出」，2026-09-03 之后改成长连接可编排对端，以支撑 L2 场景编排器
（`scripts/l2/`）。

## 应答策略

| 模式 | 行为 |
| --- | --- |
| `Auto`（默认） | 请求一到就按正常车辆应答 |
| `Manual` | 挂起，等场景调 `/answer/{key}` |
| `Silent` | 永不应答——这就是站点操作跑掉自己那个操作员超时时，服务端看到的样子 |

四类请求各自独立设：`sublot`、`loadResult`、`unloadResult`、`safetyCheck`。

## 控制面

`http://127.0.0.1:58009/control/v1`，机器契约见 `openapi.json`。

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health`、`/snapshot`、`/openapi.json` | 读状态 |
| `PUT` | `/policy` | 改四类请求的应答策略 |
| `PUT` | `/safety` | 报新的安全状态（发 `SafetyStateChanged`） |
| `PUT` | `/answer/{key}` | 应答一条挂起的请求 |

`/snapshot` 的 `readiness`：握手前与掉线后是 `DISCONNECTED`，服务端授予后是 `READY`，读循环
挂了是 `FAULTED`（`readinessReasonCode` 带异常）。**`FAULTED` 这个状态是踩坑踩出来的**——一个
在处理函数里抛出的异常会静悄悄地弄死读循环，场景只会看到自己在某个阶段一直等到超时，什么线索
都没有。

`/answer/{key}` 的 key：`sublot`、`safety-check`、`operation:{slotOperationAttemptId}`，
从 `/snapshot` 的 `pending` 里读准确值。

## 两条载重的实现细节

**答案按 key 缓存，重发时原样再送。**ControlServer 每个轮询周期都会重发未结的命令。ADR-cross-0006
与 ADR-cross-0014 要求对端返回**已有**结果而不是产生新的：同一个 `slotOperationAttemptId` 下换一个
`resultId` 的第二份 OperationResult 是内容冲突，服务端会直接把会话拆掉——第一次跑就是这么挂的。

**`resultContentSha256` 是契约，不是校验和。**服务端用同一套字段和同一个顺序重算，对不上就整条
拒收。所以业务内容只构造一次，序列化两遍（一遍算哈希、一遍上线）；构造两遍就是让两者悄悄分叉。

## 边界

`/safety` 只能说明车载端**观测到**什么，`/answer` 只能应答服务端已经发来的请求。这里没有任何
「让服务端以为装载完成了」的捷径——那种捷径测的只是脚本自己。
