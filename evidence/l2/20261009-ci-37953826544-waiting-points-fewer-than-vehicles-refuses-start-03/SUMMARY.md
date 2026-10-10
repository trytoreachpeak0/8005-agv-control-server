# L2 场景证据：waiting-points-fewer-than-vehicles-refuses-start

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163659357Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| protocolReleaseIdentity.source | `appsettings.json:ProtocolCandidate` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163659357Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 库里有一版经 FieldOps 导入的登记，25 号图上正好一个启用等待点（站 214） | PASS | `Versions=1 EnabledOnMap=1 Stations=214` | `Versions=1 EnabledOnMap=1 Stations=214` |
| 服务端进程以非零退出码结束，/health/live 始终不通 | PASS | `非零、从未答过` | `exit=-532462766 everLive=False` |
| 服务端日志含 WAITING_POINTS_FEWER_THAN_VEHICLES，写明车辆数 2、可分到的点数 1、没分到点的车，命令的 --database 是本轮 stage 的库文件 | PASS | `WAITING_POINTS_FEWER_THAN_VEHICLES + has 2 vehicles + gives only 1 of them + left without a point: BROKERX-L2-0002 + --database "C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163659357Z-slot3\controlserver.db" + --map 25` | `[00:37:12 ERR] Waiting point registration refused: WAITING_POINTS_FEWER_THAN_VEHICLES JourneyRuntime:Fleet has 2 vehicles, but waiting point registration version 1 gives only 1 of them a waiting point of their own on Map 25 (1 enabled there). Short by 1; left without a point: BROKERX-L2-0002. No database edit is needed. Either register or enable at least 1 more waiting point(s) on Map 25 that these vehicles' whitelists admit, with the server stopped, then start it again: ControlServer.FieldOps.exe import-waiting-points --database "C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163659357Z-slot3\controlserver.db" --input <waiting-points.csv> --catalog <stations-25.json> --map 25 --fleet "BROKERX-L2-0001;BROKERX-L2-0002" (add --dry-run to preview; read-waiting-points shows the current registration). Or take vehicles out of JourneyRuntime:Fleet until the registration covers the rest (1 can be covered now; one vehicle is not checked). See docs/field/batch-8-waiting-point-registration.md. A single-vehicle deployment (JourneyRuntime:Fleet empty or one vehicle) is not subject to this check.` |
| 另等 15 秒：库里没有受理的需求与订单意图，假 RIoT 没收到任何建单 | PASS | `Accepted=0 Intents=0 Orders=0` | `Accepted=0 Intents=0 Orders=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
