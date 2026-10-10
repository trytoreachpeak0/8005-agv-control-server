# L2 场景证据：stop-ended-journey-continues

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T164710120Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T164710120Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 站点等待到期：乙被终结，甲仍在车上，旅程没有收尾（B 形态） | PASS | `B TERMINATED, A LOADED, not Completed` | `B TERMINATED, A LOADED, AwaitingStationDeparture CLOSED/CARGO_HOLDING_TIMEOUT` |
| 车收到并确认了一张 11 号站的空清单：号比此前每一版都大，作业会话与期限为 null | PASS | `one acknowledged empty worklist above every other` | `1@N1-3_N1-7x1+ 2@C15-13x1+ 3@C15-13x0+` |
| 迟到的扫码得到 SublotRejected / WORKLIST_REVISION_STALE：demandId 为 null，currentWorklistRevision 是空清单的号 | PASS | `WORKLIST_REVISION_STALE @ 3` | `WORKLIST_REVISION_STALE demand= @ 3` |
| 迟到的取消被拒，原因码 WORKLIST_REVISION_STALE（不再是误导人去查授权的 ACTION_NOT_ALLOWED_IN_STATE） | PASS | `REJECTED / WORKLIST_REVISION_STALE` | `REJECTED / WORKLIST_REVISION_STALE` |
| 关卡那一版清单的号在空清单之上，整条清单流没有两版同号 | PASS | `gate above the empty one, all distinct` | `gate stop 关卡; 1@N1-3_N1-7x1+ 2@C15-13x1+ 3@C15-13x0+ 5@关卡x1+` |
| 从到 11 号站起会话没断过：合成车载端的会话代不变、没进过 FAULTED，旅程在关卡卸完收尾 | PASS | `generation 1, Completed` | `generation 1 (READY), Completed CLOSED/CARGO_HOLDING_TIMEOUT` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
