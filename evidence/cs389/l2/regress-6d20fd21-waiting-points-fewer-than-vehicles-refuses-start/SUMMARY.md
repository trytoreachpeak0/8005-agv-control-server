# L2 场景证据：waiting-points-fewer-than-vehicles-refuses-start

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260929T135639494Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `6d20fd2169a3f0818755d1340ed530481ec6cdb2` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| protocolReleaseIdentity.source | `appsettings.json:ProtocolCandidate` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260929T135639494Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 库里有一版经 FieldOps 导入的登记，25 号图上正好一个启用等待点（站 214） | PASS | `Versions=1 EnabledOnMap=1 Stations=214` | `Versions=1 EnabledOnMap=1 Stations=214` |
| 服务端进程以非零退出码结束，/health/live 始终不通 | PASS | `非零、从未答过` | `exit=-532462766 everLive=False` |
| 服务端日志含 WAITING_POINTS_FEWER_THAN_VEHICLES，写明车辆数 2、可分到的点数 1、没分到点的车，命令的 --database 是本轮 stage 的库文件 | PASS | `WAITING_POINTS_FEWER_THAN_VEHICLES + has 2 vehicles + gives only 1 of them + left without a point: BROKERX-L2-0002 + --database "C:\Users\szy\AppData\Local\Temp\l2-20260929T135639494Z\controlserver.db" + --map 25` | `[21:57:12 ERR] Waiting point registration refused: WAITING_POINTS_FEWER_THAN_VEHICLES JourneyRuntime:Fleet has 2 vehicles, but waiting point registration version 1 gives only 1 of them a waiting point of their own on Map 25 (1 enabled there). Short by 1; left without a point: BROKERX-L2-0002. No database edit is needed. Either register or enable at least 1 more waiting point(s) on Map 25 that these vehicles' whitelists admit, with the server stopped, then start it again: ControlServer.FieldOps.exe import-waiting-points --database "C:\Users\szy\AppData\Local\Temp\l2-20260929T135639494Z\controlserver.db" --input <waiting-points.csv> --catalog <stations-25.json> --map 25 --fleet "BROKERX-L2-0001;BROKERX-L2-0002" (add --dry-run to preview; read-waiting-points shows the current registration). Or take vehicles out of JourneyRuntime:Fleet until the registration covers the rest (1 can be covered now; one vehicle is not checked). See docs/field/batch-8-waiting-point-registration.md. A single-vehicle deployment (JourneyRuntime:Fleet empty or one vehicle) is not subject to this check.` |
| 另等 15 秒：库里没有受理的需求与订单意图，假 RIoT 没收到任何建单 | PASS | `Accepted=0 Intents=0 Orders=0` | `Accepted=0 Intents=0 Orders=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
