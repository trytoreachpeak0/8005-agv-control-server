# cs#566 现场续场二小结（2026-10-11，agv02／agv03，V2 并行实例）

服务端 `da6e40e2`（本地出包 `WireToGate-20261011-022635-da6e40e.zip`，sha256 `fdba6a51…`，见 `../20261011-W2-prep2/*.zip.sha256`）、车载端 `c60013ae`、protocol-v3.0.0。factory01 上的 V2 实例（58105／58107），只为 agv02 建单；agv01、MVP 服务、生产 MesIngest 都没碰，需求全部由 FakeMesIngest 注入。

## 结论

| 段 | 结果 | 出处 |
| --- | --- | --- |
| C 多需求持货与追加 | **走通**：持货等单、乙追加进同一旅程、关卡卸两条均 `Succeeded`，无人工处置。两车让站未做（cs#600） | `../20261011-W2-cargo-holding/SUMMARY.md` |
| E 空闲返回与等待点独占 | **没走通**：空闲返回建单两次都回 HTTP 200＋业务码 `00002`，旅程停在结果未知（cs#611），根因是 upperId 超长（cs#616） | `../20261011-W2-idle-return/`、`../20261011-00002-investigation/` |
| F 桩 211 充电周期 | **没走通**：充电分配成功，充电单同样 `00002`（71 字符 upperId，cs#616），120 秒后按 2255 放弃；随后用户在 RIoT 网页手派一单，结束后 V2 永远读不出结束、一直按住 agv02（cs#617） | `../20261011-W3-S1-charging/` |

E、F 在当前代码下跑不通，今天不再重试，等 cs#616、cs#617（以及 cs#611）修复后另开现场。

## 今天引出的票

| 票 | 内容 | 现场出处 |
| --- | --- | --- |
| cs#600 | 车辆安全投影只认单车 `VehicleKey`，两车实例里第二台车永不就绪；V2 改回单车 | `../20261011-W2-prep2/20b-single-car-note.md` |
| cs#605 | 原地升级脚本 `Update-ControlServerLocal.ps1:307` 对空值取 `.Count` 抛错 | `../20261011-W2-prep2/19-upgrade-single-car.txt` |
| cs#609 | 持货等待期限默认 30 分钟改 5 分钟（用户定） | C 段 12:15～12:35 车在站 4 等了约 20 分钟 |
| cs#611 | 建单 `00002` 后结果未知无限期对账，车一直算忙 | `../20261011-W2-idle-return/08-stuck-idle-return-db-state-cs611.txt` |
| cs#614 | 用户改 26 号图站名（T04-0x→T4-x）后准入漂移 2116，factory01 手改准入版本 4，定义需跟进 | `../20261011-W2-admission-rebind-v4/README.md` |
| cs#616 | 空闲返回／充电 upperId 69～71 字符，RIoT 回 `00002`；搬运单 47 字符正常 | `../20261011-00002-investigation/` |
| cs#617 | 无 upperId 的外来单：RIoT 答 HTTP 200、`orderState=2`、`upperId` 空串，SDK `ToOrderRef` 抛 `order-ref-missing`，V2 当未知、2187 永久按住 | `../20261011-W3-S1-charging/11-foreign-order-hold.txt`、`13-foreign-order-readback.txt` |
| hmi#304 | 车载屏操作记录时刻显示为 UTC | `../20261011-W2-cargo-holding/08-hmi-screenshot-1209-after-append.png` |

另：工作区 PR #15（已合入 `b7146d65`）修了 19 号脚本 `try{}; catch{}` 拼接解析错误，并填了 agv03 的身份值。

## 三次换库与快路线

1. 10:28 第一次：19 号卸载（保留数据）→ 旧库目录改名 → 19 号安装新库（`../20261011-W2-prep2/`）。
2. 14:51 第二次：同样走 19 号重装（`../20261011-W2-prep3-newdb/`）。同包换库其实不需要重装，这次多走了一遍。
3. 15:08 第三次，快路线（`../20261011-W2-prep4-fast-newdb/`）：停 V2 → 数据目录改名、建同 ACL 空目录 → 启 V2（Host 启动即迁移，空目录自动建库并首绑）→ 对运行中的库做 FieldOps 导入 → 清 agv02 车载端日志簿。约 8～10 分钟，不碰安装目录与定义。

每次换库都同时清了 agv02 车载端日志簿，否则会 `SNAPSHOT_REVISION_CONTENT_CONFLICT`。旧库全部留存：`C:\ProgramData\8005\ControlServer.V2.pre-cs566b-20261011`、`…pre-cs566b-newdb2-20261011`、`…pre-cs566b-newdb3-20261011`。

## 收工状态（15:26～15:28 实读）

| 项 | 状态 | 出处 |
| --- | --- | --- |
| 闸门 `RiotCreateDispatch.enabled` | `false`（15:19:29 关，收工改配置前后回读都是 false） | `12-gate-close.txt`、`15-close-waits-stop-v2.txt` |
| 等待时长 | `stationDepartureWaitTimeout=00:05:00`、`cargoHoldingTimeout=00:05:00`（生产值，回读） | `15-close-waits-stop-v2.txt` |
| 充电桩名册 | v3 空名册（关窗） | `14-close-roster-policy.txt` |
| 充电策略 | 换回 v1（强制线 30、完成线 80），临时 v2 不再生效 | `14-close-roster-policy.txt` |
| 211 独占 | **仍为 `RESERVED`**（名册 v2、journeyId `charging:…:20261011T071230770Z`），是充电单放弃后的残留；按要求照实记录，没有手改库。导入工具提示窗口不能完全关闭，直到这条预留消失 | `14-close-roster-policy.txt` |
| agv02 在 V2 里 | 被外来单 `order-2109181133127286784` 以 2187（Unsettled）按住，cs#617 | `11-foreign-order-hold.txt` |
| V2 服务 | **已停**，启动类型 **Manual**（用户本会话选定）；MVP 进程 pid 前后不变 | `15-close-waits-stop-v2.txt` |
| agv02 | 车载端与模拟器已停；IO 仍指向模拟器（`ioModule.host=127.0.0.1`）；RIoT 读 216、`MT_FINISHED`、`IDLE`，无 W2G 非终态单 | `16-agv02-stack-stop.txt`、`17-final-state.txt` |
| agv03 | 车载端已部署、未启动（无进程） | `17-final-state.txt`、`../20261011-W2-prep2/16-agv03-deploy-onboard.txt` |

## 未入库

`20261011-W2-prep2/release-manifest.json`、`SHA256SUMS.txt`（约 420 KB，同一提交出包即可重生成）；zip 的 sha256 已在 `*.zip.sha256`。
