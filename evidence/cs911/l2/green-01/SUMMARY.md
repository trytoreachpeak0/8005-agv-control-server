# L2 场景证据：staging-to-wire-second-leg

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261123T171820279Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d6442f083f192403444a2ceaa0823703889d0f01` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `7.0.0` |
| protocolReleaseIdentity.tag | `protocol-v7.0.0` |
| protocolReleaseIdentity.commit | `a1facf8674b05c67bfbddc14db4a163e8da2782a` |
| protocolReleaseIdentity.protocolVersion | `8` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `44fa0b273810fb2d5326068c03f9647bfd8fdc1c5f01e27a5177b0e0473fe7cb` |
| protocolReleaseIdentity.schemaBundleSha256 | `6361d734f9b1a4f81178c79c0bc18b8287fd747622e60c99073e7fd5a5cf4c88` |
| protocolReleaseIdentity.vectorsSha256 | `6295840ec27422dc024a24f6deae9a7379f9b3047a163e903ac862c8b0274eec` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261123T171820279Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 焊线那程受理并走完，受理行的程是焊线 | PASS | `Completed, leg 焊线, Succeeded` | `Completed, leg '焊线', Succeeded` |
| 同一程（STEP 焊线）以新 DemandId 再发：不受理，积压原因 TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED | PASS | `TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED, never accepted` | `backlog TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED, accepted False` |
| 焊线2 那程受理并走完，受理行的程是焊线2 | PASS | `Completed, key L2-S2W2-20261123T171820279Z\|STAGING_TO_WIRE, leg 焊线2, Succeeded` | `Completed, key L2-S2W2-20261123T171820279Z\|STAGING_TO_WIRE, leg '焊线2', Succeeded` |
| 同一业务键下两条完成事实，程分别是焊线与焊线2，各是自己那条需求的 | PASS | `焊线=b6952434-6141-4460-b8f0-3847c9c398c6 焊线2=b15c1439-42c5-40f4-9f99-4d041ec34f33` | `2 row(s): 焊线=b6952434-6141-4460-b8f0-3847c9c398c6 焊线2=b15c1439-42c5-40f4-9f99-4d041ec34f33` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
