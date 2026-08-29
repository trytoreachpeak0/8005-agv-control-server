# 到站之后首次走通：三处服务端修复的现场验证

## 运行类型

`AUTHORIZED_SINGLE_REAL_CREATE`。用户给出现场物理安全 GO 与逐次建单授权，本次验证
[跨会话代次重放快照造成自我维持的重连死循环](../../docs/defects/20260829-snapshot-replay-across-session-generations.md)
之后的三处服务端修复，目标是把旅程推过到站进入取货阶段。

## 结论

**到站之后的第一段首次走通。** 旅程从 `AwaitingPickupArrival` 前进到 `AwaitingSublot`，
三条旅程快照全部被车载确认，`SublotEntryRequested` 首次真实发出。

## 修复前后对比（同一台车、同一条 Demand、同样观察时长）

| 指标 | 修复前 `arrival3`（`8caf746` + 车载 `84b7f3f`） | 本次（`f94483c` + 车载 `304e6ad`） |
| --- | --- | --- |
| `SessionHello`（会话建立次数） | 22 | **1** |
| `ProtocolContentConflictException` | 51 | **0** |
| 车载 `ProtocolProblem` | 20 条 `SNAPSHOT_REVISION_CONTENT_CONFLICT` | **0** |
| 快照被确认 | 0 / 3 | **3 / 3** |
| `SublotEntryRequested` | 从未发出 | **已发出** |
| `Stage` | `AwaitingPickupArrival` | **`AwaitingSublot`** |
| `SessionGeneration` | 涨到 21+ | **停在 1** |

运行态：`Readiness=Ready`、`ReasonCode=READY`、`DepartureSafe=1`、`BlockReasonCode` 为空、
`hostStderrEmpty=true`、四端口回收。日志中唯一异常是 15:49:03 脚本收尾杀进程导致的 socket
关闭，非产品缺陷。

## 三处修复与其现场效果

### 1. 出站 wire 必须可被对端逐字节复现（`b426ef6`）

服务端把快照 payload 作为 CLR 对象一次性序列化，`DateTimeOffset` 转换器原样写出时区的 `+`；
对端把行解析为 payload 仍是 `JsonElement` 的信封后重新序列化，该字符经 encoder 转义。JSON 语义
相同、字节不同，而 `AcknowledgeOutboundEnvelopeAsync` 要求逐字节复现，故到站后第一条
`SnapshotAppliedAck` 被拒并拆掉连接。

现场字节取证（离线、只读，从 `arrival3` 的 SQLite 与车载 journal 还原）：

| 项 | 值 |
| --- | --- |
| 车载 ack 中的 `appliedContentSha256` | `61fd35a952b39688fe7fda9f46d8e68efdb63aadd94eac7032b1c0b5c86429f7` |
| 服务端第 1 代实际写库并发出的 wire（616 字节，与 EF 日志 `@p5 Size = 616` 吻合） | `e36ed81520864cf9ffe13867999910a11ed2cac3c22ebe2c47c47d3a3dfef6aa` |
| 同一 wire 仅把 `observedAt` 的 `+` 换成其六字符 unicode 转义（621 字节） | **命中车载哈希** |

修法：序列化信封前先把 payload 物化为 `JsonElement`，与协议契约类型自身的构造方式一致。

### 2. 重放不得改动冻结的 `sentAt`（同一提交）

`ReplayPendingForSessionAsync` 用新时钟改写 `sentAt` 与 `CreatedAt`，但快照 payload 的
`observedAt` 是**从 `sentAt` 推导**的（`8caf746` 引入），因此重绑后的 payload 保留旧
`observedAt`，下一轮发布算出新值，`RefreshOutboundEnvelopeAsync` 判定语义冲突并按
「非推进的会话代次」拒绝，每个运行时迭代各抛一次。重绑属传输关注点，现在只推进代次。

> 更正 2026-08-29 上一条进度中的推断：该异常**不是**由 `+` 字节漂移造成的。回归测试当场
> 证伪了那个假设，真实原因是上述 `sentAt` / `observedAt` 不一致。

### 3. MesIngest 需求标识归一化（`f94483c`）

MesIngest 报的 `demandId` 不带连字符，而所有承载 `demandId` 的 WIRE_TO_GATE payload 都是规范
UUID，服务端读取的入站 `demandId` 也都先按 UUID 解析再与库中值比对，故两者永不相等：取货阶段
每个运行时迭代都以 `DemandId must be a UUID` fail closed，`SublotEntryRequested` 从未发出。
现在在 ingest 边界归一化一次。

**副作用（有意接受）**：`upperId` 与旅程的稳定 GUID 均由 `demandId` 派生，故一并变形：

```
旧: W2G-94993971b3624edf81bc712d160e444a-PICKUP-1
新: W2G-94993971-b362-4edf-81bc-712d160e444a-PICKUP-1
```

本次运行前车辆 `procState`/`sysState` 均为 `IDLE`、无在途订单，因此按预期新建单而非叠单；若旧
订单未终结，`RIOT_NONFINAL_ORDER_PRESENT` 会先行阻断。

## 跨仓互操作验证（生产代码对生产代码）

由真实 `OnboardJourneyPublisher` 产出 wire，交由真实 `WireToGateProtocolSerializer` 计算
ack 哈希，两侧均为生产程序集，无任何替身实现：

| 阶段 | 服务端存储哈希 | 车载 ack 哈希 | 结果 |
| --- | --- | --- | --- |
| 第 1 代首发 | `b960f6e3ab01…` | `b960f6e3ab01…` | ACK 接受 |
| 重连后重绑到第 2 代 | `fb7738f9f3b4…` | `fb7738f9f3b4…` | ACK 接受 |
| 第 2 代再次发布 | `fb7738f9f3b4…` | `fb7738f9f3b4…` | ACK 接受 |

同时验证：重放后再发布为字节完全相同的 no-op；王昆 `304e6ad` 用的 payload 身份在跨代次后保持
不变，即两端修复互通，不再触发 `SNAPSHOT_REVISION_CONTENT_CONFLICT`。

## 物理侧确认

- 订单 `order-2093605636779671552`，`upperId = W2G-94993971-b362-4edf-81bc-712d160e444a-PICKUP-1`，
  `OrderIntents.Status = CONFIRMED`
- 车辆由 210（关卡）移动到 21（`N2-5_N3-5` 取货站），终态 `MT_FINISHED`、`speed 0`、`IDLE`、空载
- 运行前只读复核前置：`movementState=MT_FINISHED`、`emergencyState=OK`、
  `breakSwitchState=MOVABLE`、`locationState=LOCATION_STATE_RUNNING`、无在途订单
- 电量由 38% 降至 36%

## 门禁结果

| 项 | 结果 |
| --- | --- |
| Release 构建 | 0 warning / 0 error |
| `dotnet format --verify-no-changes` | PASS |
| 完整测试 | 222 passed / 0 failed / **0 skipped** |
| 新增回归测试红绿验证 | 去掉修复 2 红，加回 2 绿 |
| W2G-IS-00～07 八片 G2 | 全部 PASS，合计 129 个筛选测试、0 skip |

八片 G2 均绑定 `implementationCommit = f94483c14448e06c87adf04f3e870c7aba0e818a` 与 manifest
`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`，机器证据在忽略目录
`artifacts/g2/issue10-f94483c/`。八份 `gate-result.json` 的集合 SHA-256 为
`0b887c0898549d1c68ae257eb68edb6d1903f77de9f29ed770adf8e614176e21`，算法为：按相对路径序数
排序，每行 `<相对路径（正斜杠）><两个空格><文件 SHA-256 小写十六进制>`，以 `\n` 连接后取
UTF-8 SHA-256。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@f94483c14448e06c87adf04f3e870c7aba0e818a`
- 部署包：`controlserver-package-f94483c`（由该提交 `dotnet publish -c Release` 产出，81 个文件），
  内容集合 SHA-256 `b75016e4155941013880f46c25023d1f7b80395a8ec7a098347fca85782e4618`，算法同上
- OnboardHmi：`OnboardHmi_MVP@304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6`（王昆的
  `Fix snapshot revision replay across sessions`，一次性克隆 detached 重建）
- slots-simulator：`main@fb5f7c5`
- 协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- 隔离运行：全新 SQLite 与 journal、临时端口 58105／58107，已安装服务全程未停未改且其 runtime
  与建单开关保持关闭

## 未走通的部分

`SublotEntryRequested` **未被确认**，这不是缺陷——其 `entryMethods` 为 `SCANNER` / `KEYBOARD`，
在等现场操作员在 HMI 上扫码或键入子批号。车辆停在取货站等待该人工步骤。

其后的装货、发车安全检查、`TO_GATE` 移动、关卡批量卸货与四事实原子完成均未进行。

W2G-IS-00～07 的正式 G3 与 RC 保持 `INCONCLUSIVE`。
