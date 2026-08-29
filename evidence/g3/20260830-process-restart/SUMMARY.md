# 2026-08-30 阶段性 G3：进程崩溃重启向量

## 结论

`STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT` 运行 `20260829T172519575Z` 通过，
二十条断言全部 PASS。本结果不是任一完整切片的正式 G3 PASS；`W2G-IS-00` 与
`W2G-IS-06` 仍保持 `INCONCLUSIVE`。

本次没有创建 RIoT 订单，没有发送移动命令，没有伪造停稳/驻车信号，也没有使用现场凭据。
真实 OnboardHmi 组合根继续使用 `UnavailableVehicleSafetySignalProvider`，三个阶段都收敛到
`RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`，ControlServer `/health/ready` 保持 HTTP 503。

该向量此前由规划仓 `.scratch/` 下的独立脚本 `run-staged-g3-no-movement.ps1` 承担，绑定
`cc6e2b9` + `0455147`，比当时的双端落后两代。本次把它归位为 ControlServer 仓
`scripts/run-staged-g3-restart.ps1`，并取消了脚本内的 commit 重述。

## 冻结身份

- ControlServer：`3d8b00c7558ae700358f1f995a5ac75d12a3250c`
- OnboardHmi：`304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6`
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest SHA-256：`a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- runner：`9def7f8f16609ec593e6f79fbfe9124ef165b7d1`，起跑时工作区干净
- 运行配置 SHA-256：`496c201269ca91edcf9789b51e085ea4c3b47bbfb6e9d7bab4431058a77ad27d`

四个 peer commit 不在本 runner 内重述：它解析 `scripts/run-staged-g3.ps1` 的 `param()` 块，
读回 `$ControlServerCommit`、`$OnboardCommit`、`$SimulatorCommit`、`$ProtocolCommit`
四个默认值（来源文件 SHA-256 `6c91b0b94058…`，记录在 `configuration.json`）。因此两个 runner
不可能绑到不同的 peer，这正是旧脚本漂移两代的成因。

## 运行观察

| 阶段 | session generation | OnboardHmi PID | ControlServer PID |
| --- | --- | --- | --- |
| 全新首连（全新 SQLite + 全新 journal） | 1 | 17220 | 34960 |
| 车载进程重启，复用同一 journal | 2 | **32432** | 34960 |
| 服务端进程重启，复用同一 SQLite | 3 | 32432 | **22192** |

每个阶段之后按秒采样十二次，十二个样本的 generation 全部等于该阶段值——单次事后快照分不出
「稳定」与「悄悄重连后落回同一个数」，逐秒采样可以。被替换的那个进程在下一阶段开始前均已
确认退出。

跨重启的持久身份：

- 车载 journal epoch `b8c04896-9088-4721-ab36-8a75616ecc37` 在车载重启前后一致，文件创建时间不变；
- 车载 durable outbox 从 2 行增至 5 行，重启前的 2 行按 `(dedup key, messageId, contentSha256)`
  原样保留；
- 服务端 `ProtocolInbox` 从 14 行增至 20 行，服务端重启前的 14 行按 `(messageId, messageType,
  contentHash)` 原样保留，数据库文件创建时间不变；
- 三条 `RecoveryStateReport` 的 messageId 在两端各自的存储里是同一个集合
  （`5454dc42…`、`b37b6ceb…`、`cf889263…`），且车载侧全部已 ack；
- `SessionRecoveries` 始终是每 AGV 一行，最终 generation 为 3。

`SessionAccepted` 的 `serverInstanceId` 三次各不相同。这不是「服务端重启了三次」：
`OnboardMessageProcessor` 注册为 `Scoped`，`OnboardTcpServer` 每接受一条连接开一个 scope，
所以该 id 是**每连接**一个。重启事实由 OS 进程身份承担，连接身份另作为独立不变量断言。

## 本向量取不到的部分

`VehicleDispatchLeases` 以 `DemandId` 为主键，没有已受理 demand 就没有租约行；本运行不建单，
因此「跨重启复用同一 Demand 与车辆租约」在本形态下不可达，已具名记录在
`configuration.json` 的 `vectorsNotReachableWithoutAnAcceptedDemand`，随带 demand 的运行一并取证。

## 两次作废的运行

正式结果之前有两次运行被判作废，两次都是 runner 证据缺陷，不是产品 FAIL：

1. `Invoke-SqliteRows` 在只匹配到一行时返回裸 `[ordered]` 而非数组，`rows[0]` 于是按位取到了
   第一个**值**，三条读 `SessionRecoveries` 的断言在数据库其实正确的情况下变红；同一次运行还
   暴露出 `serverInstanceId` 被误当作每进程身份。
2. 上一条的修复（`return ,$rows`）让函数以单个对象输出行数组，外层残留的 `@()` 把数组又套深
   一层，journal epoch 在前后两次读取都成了 `null`。

两次都是**断言抓到的**，不是靠猜。

## 证据

- [`run-result.json`](run-result.json)：机器可读运行结果，SHA-256
  `397facacc78e090f90f175ab193358efba8f9d8f706fdf772806ad7be9453484`
- [`configuration.json`](configuration.json)：绑定来源、端口、不可达向量清单
- `logs/`：克隆/检出/发布日志，两代 ControlServer 与两代 OnboardHmi 的 stdout/stderr，
  模拟器日志，车载结构化日志
- `run-result.json` 的 `evidenceFiles` 收录本目录其余 24 个文件的 `path/sha256/length`

## 仍未通过

- 本运行只覆盖进程重启与会话代际，不覆盖业务旅程；带 demand 的重启形态仍缺；
- `W2G-IS-00`／`W2G-IS-06` 的完整向量、真实移动闭环与现场验收均未宣称通过；
- 完整 G3 与 RC 仍为 `INCONCLUSIVE`。
