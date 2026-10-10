# L2 场景证据：real-onboard-unread-pre-create-read

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163207932Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37953803085-1\_stage\l2-20261009T163207932Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 注入只打中一次，打中的是 TO_PICKUP 那张单的读 | PASS | `[0] W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1 / remaining 0` | `[W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1] / remaining 0` |
| 读没读到之后，TO_PICKUP 照常建出并确认，建单计数恰好是 1 | PASS | `CONFIRMED / 1` | `CONFIRMED / 1` |
| TO_PICKUP 审计链：建单前读没读到 → 建单前读答没有 → 才 arm 建单；只 arm 一次 | PASS | `PRE/UNKNOWN/SdkFailure -> PRE/NOT_FOUND -> CREATE_DISPATCH/ARMED (x1)` | `PRE_CREATE_RECONCILIATION/UNKNOWN/SdkFailure/RIOT_API_FAILURE -> PRE_CREATE_RECONCILIATION/NOT_FOUND/NotFound/ -> CREATE_DISPATCH/ARMED// -> CREATE_REQUEST/STARTED// -> CREATE_RESPONSE/ACCEPTED/SdkAccepted/ -> POST_CREATE_RECONCILIATION/CONFIRMED/Found/` |
| RIoT 上只有一张单，就是取货腿那一张 | PASS | `1 / W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1` | `1 / W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1` |
| 补建的取货单走完：服务端采信到站，真车载端开放录入 | PASS | `AwaitingSublot / can submit` | `AwaitingSublot / True` |
| 注入只打中一次，打中的是 TO_GATE 那张单的读 | PASS | `[1] W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-GATE-1 / remaining 0` | `[W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1,W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-GATE-1] / remaining 0` |
| 读没读到之后，TO_GATE 照常建出并确认，建单计数恰好是 1 | PASS | `CONFIRMED / 1` | `CONFIRMED / 1` |
| TO_GATE 审计链：建单前读没读到 → 建单前读答没有 → 才 arm 建单；只 arm 一次 | PASS | `PRE/UNKNOWN/SdkFailure -> PRE/NOT_FOUND -> CREATE_DISPATCH/ARMED (x1)` | `PRE_CREATE_RECONCILIATION/UNKNOWN/SdkFailure/RIOT_API_FAILURE -> PRE_CREATE_RECONCILIATION/NOT_FOUND/NotFound/ -> CREATE_DISPATCH/ARMED// -> CREATE_REQUEST/STARTED// -> CREATE_RESPONSE/ACCEPTED/SdkAccepted/ -> POST_CREATE_RECONCILIATION/CONFIRMED/Found/` |
| RIoT 上共两张单：取货腿一张、关卡腿一张 | PASS | `W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-GATE-1, W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1` | `W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-GATE-1, W2G-c32dfbbc-f641-4bff-be08-dc218c8e8863-PICKUP-1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
