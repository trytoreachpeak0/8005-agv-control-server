# ControlServer.FakeMesIngest

MesIngest V2 的测试替身，只替 WIRE_TO_GATE 运行时真正读的那三个端点：契约发现、外部可读需求目录、
`SUBLOT_BOX_COUNT`。**它不替 MesIngest**——那是 `8005-mes-ingest` 的事。

```bash
dotnet run --project tools/ControlServer.FakeMesIngest
```

默认监听 `127.0.0.1:58088`，非 loopback 默认拒绝启动。没有 `appsettings.json`（原因见
`ControlServer.FakeRiot/README.md`），配置走命令行开关。

让 ControlServer 连过来：`MesIngest:baseUrl` 指到这里即可。本工具不校验 Bearer——凭据不是它要
证明的东西。

## 数据面

| 方法 | 路径 |
| --- | --- |
| `GET` | `/api/v2/contract` |
| `GET` | `/api/v2/externally-readable-demand-catalog` |
| `GET` | `/api/v2/sublot-box-count?sublot=` |

三处必须较真的地方，都是因为 ControlServer 会较真：

- **契约发现是目录读取的前置。**`contractVersion`、`schemaVersion` 和整个能力集必须逐项相等，
  差一点就 `CONTRACT_VERSION_MISMATCH`，目录根本不会被读。`PUT /control/v1/contract`
  的 `breakContract=true` 就是用来演这一幕的。
- **目录的 ETag 由 body 推导**（`W/"catalog-h{historyEpoch:N}-r{catalogRevision}"`），服务端会
  重算并比对。算错了和目录损坏在服务端看来没有区别。
- **demandId 必须唯一且按序数排序**，否则整份目录被拒。

还有一个刻意保留的不规整：**demandId 用不带连字符的写法**。服务端在入口处归一化成规范 UUID，
替身要是直接发规范写法，那段代码就再也没人覆盖了。

## 控制面

`http://127.0.0.1:58088/control/v1`，机器契约见 `openapi.json`。约定与其余替身一致
（`runId` / `commandId` 幂等 / `expectedRevision` / 稳定 `reasonCode` / `revision` 只在真变化时递增），
细节见 `ControlServer.FakeRiot/README.md`。

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| `GET` | `/health`、`/snapshot`、`/openapi.json` | 读状态 |
| `POST` | `/reset` | 新一轮，目录清空 |
| `PUT` | `/demands/{demandId}` | 新增或替换一条需求 |
| `DELETE` | `/demands/{demandId}` | 从目录里撤掉一条 |
| `PUT` | `/contract` | 让契约发现报一个服务端不接受的版本 |

`sublot`、`area`、`eqp`、`package` 没有默认值，首次必须给；其余字段留空就沿用现值或取默认。

**`area` 要填站点名里解析得出的区号，不是它的前缀。**`MapStationResolver` 把 `N1-3_N1-7` 拆成
`N1-3` 与 `N1-7` 两个区号，填 `N1` 会判 `AREA_STATION_NOT_FOUND`。

## 边界

目录初始为空，是刻意的：场景说明有哪些需求，替身自带一条需求会让「没有任何需求被受理」这条断言
无法成立。
