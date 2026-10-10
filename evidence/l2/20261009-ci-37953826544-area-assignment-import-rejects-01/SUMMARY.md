# L2 场景证据：area-assignment-import-rejects

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160337915Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160337915Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 已发布整车模型把八个仓位分成 FRONT／REAR 两组，导入的合法分组取值就是从这里读的 | PASS | `OK / FRONT,REAR` | `OK / FRONT,REAR` |
| 服务端以目标配置起过一次，调度策略里有分区 MAP-25-WIRE_TO_GATE；导入判「分区存在」就按这张表 | PASS | `>= 1` | `1` |
| 导入之前库里一版分区归属表都没有，也没有导入审计 | PASS | `0 / 0` | `0 / 0` |
| 分组取值不属于当前车型的分组集合（这里写的是改名前的 LEFT）：整份拒绝，原因码 SLOT_POSITION_NOT_IN_PUBLISHED_MODEL，库里一版都没多、没有成功导入的审计 | PASS | `1 / REJECTED / SLOT_POSITION_NOT_IN_PUBLISHED_MODEL / 0 versions / 0 audits` | `1 / REJECTED / SLOT_POSITION_NOT_IN_PUBLISHED_MODEL / 0 versions / 0 audits` |
| 缺分组：缺少分组指派的映射不成立（REQ-0191）：整份拒绝，原因码 SLOT_POSITION_MISSING，库里一版都没多、没有成功导入的审计 | PASS | `1 / REJECTED / SLOT_POSITION_MISSING / 0 versions / 0 audits` | `1 / REJECTED / SLOT_POSITION_MISSING / 0 versions / 0 audits` |
| 同一个区域号出现两次，两行还指了相反的侧：整份拒绝，原因码 AREA_DUPLICATED，库里一版都没多、没有成功导入的审计 | PASS | `1 / REJECTED / AREA_DUPLICATED / 0 versions / 0 audits` | `1 / REJECTED / AREA_DUPLICATED / 0 versions / 0 audits` |
| 区域号写法不合规，与站名里解析区域号用的是同一条规则：整份拒绝，原因码 AREA_FORMAT_INVALID，库里一版都没多、没有成功导入的审计 | PASS | `1 / REJECTED / AREA_FORMAT_INVALID / 0 versions / 0 audits` | `1 / REJECTED / AREA_FORMAT_INVALID / 0 versions / 0 audits` |
| 分区不在库内当前调度策略里：整份拒绝，原因码 DISPATCH_ZONE_NOT_FOUND，库里一版都没多、没有成功导入的审计 | PASS | `1 / REJECTED / DISPATCH_ZONE_NOT_FOUND / 0 versions / 0 audits` | `1 / REJECTED / DISPATCH_ZONE_NOT_FOUND / 0 versions / 0 audits` |
| 一份同时含五类错误的表，一次把五行全报出来，而不是停在第一处 | PASS | `AREA_DUPLICATED,AREA_FORMAT_INVALID,DISPATCH_ZONE_NOT_FOUND,SLOT_POSITION_MISSING,SLOT_POSITION_NOT_IN_PUBLISHED_MODEL` | `AREA_DUPLICATED,AREA_FORMAT_INVALID,DISPATCH_ZONE_NOT_FOUND,SLOT_POSITION_MISSING,SLOT_POSITION_NOT_IN_PUBLISHED_MODEL` |
| 先 --dry-run 过一遍：校验通过，但库里还是一版都没有 | PASS | `OK / dryRun / no version written` | `OK / True /` |
| 正确的表被收下：第 1 版落库，快照一条、业务审计一条 | PASS | `OK / v1 / 2 entries / 1 version / 1 snapshot / 1 audit` | `OK / v1 / 2 entries / 1 version / 1 snapshot / 1 audit` |
| 版本行记的内容哈希就是它那条快照的哈希，工具打出来的也是同一个 | PASS | `7ce6ad5497f34c45c893085fc92d5fe0afb0b3832425b9a2014adfe1a980b7b0` | `7ce6ad5497f34c45c893085fc92d5fe0afb0b3832425b9a2014adfe1a980b7b0 / 7ce6ad5497f34c45c893085fc92d5fe0afb0b3832425b9a2014adfe1a980b7b0` |
| 内容一模一样的一份再导入，形成第 2 版而不是被当成无变化跳过 | PASS | `v2 / same sha / 2 versions / 2 audits` | `v2 / True / 2 versions / 2 audits` |
| area-assignments 打印当前那一版的全部行，两个区域号各自指了哪一侧 | PASS | `OK / v2 / C15-13,C15-14 / FRONT,REAR` | `OK / v2 / C15-13,C15-14 / FRONT,REAR` |
| --version 1 读的是第 1 版，读这两次没有再写出任何版本或审计 | PASS | `v1 / 2 versions / 2 audits` | `v1 / 2 versions / 2 audits` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
