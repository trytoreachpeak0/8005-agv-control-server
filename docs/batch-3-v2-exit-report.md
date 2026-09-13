# 批次 3 出口报告（v2 线）：治理面单端工程

日期：2026-09-10；2026-09-12 在已发布的 `protocol-v1.0.0` 上重跑全部门禁后改写
服务端集成分支：`fp/b3-on-v2`（从 `fp/v2-impl` 的 `1f25f6e` 开出）
车载端分支：`8005-agv-onboard-hmi` 的 `w2g/b3-on-v2`
协议：`8005-agv-protocol` 的 **`protocol-v1.0.0`**（注释 tag，指向 `9f22db8`；2026-09-12 发布）

**这是批次 3 在 v2 线上的账。**v0.3.0 线（`ControlServer_MVP`）另有一份 `docs/batch-3-exit-report.md`，结论是「尚未出口」：十三票做在 v0.3.0 线，
切片与 v2 两端实现在另一条线上，两条线没有汇合。本报告记录的是汇合之后的结果：十三票搬到 v2 线，补齐协议 v2 消息面，跑完门禁与 L2。
两份报告各记各的线，不互相替代。

**2026-09-12 这份报告整体换了两次底。**当天产品负责人先后做了这些决定：

1. 看板显示一台车的全部告警（REQ-0270 原文）。
2. 车载告警接上真实来源。
3. 把单人签名规则移植到协议候选，协议从 `f6ee75d` 变为 `16e2567`。
4. 发布批准也可以由他授权的 AI agent 给出，协议因此改为 `9f22db8`，并当天发布为 `protocol-v1.0.0`，批准由 AI 给出、授权人为产品负责人。

第 3、4 两个决定各改了一次 manifest（`84f984ea…` → `25fd6689…` → `a0e1deed…`），每改一次，此前的门禁证据对两端都不再成立。
**下文每一个 `PASS` 都是在已发布的 `protocol-v1.0.0` 身份上跑出来的**，`protocolApprovalStatus` 为 `APPROVED_RELEASE`。
更早的证据原样保留，只在第四节作为历史出现。

## 结论

**默认三条出口与两个切片的四道门禁，在已发布的 `protocol-v1.0.0` 上全部成立；W1 现场逐车逐仓 IO 核对 2026-09-13 在生产现有的 v0.3.0 库上做完、W1 PASS。按规格 8.3 的出口表，批次 3 各项都已成立。**W1 做在哪条线上、怎么判的，见下表 W1 一行与第七节，读结论时要一并读到。

| 出口（规格 8.2／8.3、#18） | 状态 | 依据 |
| --- | --- | --- |
| L1 绿，新能力有新增覆盖 | **成立** | 服务端 699 passed（`6b21662`）；车载端 200 ＋ 47 passed（`98f4e06`）。逐项见第一节 |
| L2 新场景在 CI 上连续三次通过，三份证据各自独立 | **成立** | CI run `34701119449`（`a1243a8`）23/23 PASS，两个批次 3 场景各 3/3。见第二节 |
| `assertions.json` 的 `identity` 含 `protocolReleaseIdentity` 与 `batchId` | **成立** | CI 证据里 `batchId` 为 `batch-3`；`protocolReleaseIdentity` 为 `protocol-v1.0.0@9f22db8`，manifest `a0e1deed…`，`APPROVED_RELEASE` |
| `FP-IS-14` 四门禁全 PASS，与 `ProtocolReleaseIdentity` 精确绑定 | **成立** | 见第三节 |
| `FP-IS-15` 四门禁全 PASS，与 `ProtocolReleaseIdentity` 精确绑定 | **成立** | 见第三节 |
| 每次门禁 `-Output`／`-EvidenceRoot` 都是新目录，无覆盖 | **成立** | 所有纠正都写新目录，`SUMMARY.md` 指回被纠正的那份 |
| 红证据全部保留，失败原因有记录 | **成立** | 五份缺陷单加两处证据内说明，见第四节 |
| **W1：三台车逐台逐仓 IO 核对、逐台放行（#17）** | **成立（2026-09-13，在生产 v0.3.0 库上）** | `8005-agv-control-server` 分支 `feat/w1-unattended` 的 `evidence/field/20260913-W1-three-vehicle-qualification/`，W1-01～W1-06 全 PASS；24 仓真信号，现场不拍照。不是在 v2 线上做的，见第七节 |

两点保留，不影响上表但必须读到：

- **`protocol-v1.0.0` 的批准是 AI 给的。**attestation 上 `approverKind` 为 `AI_AGENT`，`authorizedBy` 为产品负责人，这是协议治理 2026-09-12
  起允许的做法。批准时，新身份上的 G2／G3／L2 证据还不存在，是发布之后才在同一个 commit 上重跑出来的，批准说明里写明了这一点。
  发布 G1 使用那份 attestation，结果 PASS，见 `evidence/g1/20260912-protocol-v1.0.0-release-9f22db8`。
- **整体 G3 仍是 `INCONCLUSIVE`。**三个 G3 runner 认领 `FP-IS-00`／`04`／`05`／`06`／`14`／`15`，`FP-IS-01`／`02`／`03`／`07` 没有可测的面。
  票 17 那八片（`FP-IS-00`～`07`）的 G2 与 G3 同日也在发布身份上重跑了，全部 PASS，见第三节。

## 一、L1

### 实测

```
服务端（fp/b3-on-v2@6b21662，干净克隆，Release）
Passed!  - Failed:     0, Passed:   699, Skipped:     0, Total:   699 - ControlServer.Tests.dll (net8.0)

车载端（w2g/b3-on-v2@98f4e06）
Passed!  - Failed:     0, Passed:   200, Skipped:     0, Total:   200 - SQCD.Agv.UnitTests.dll (net8.0)
Passed!  - Failed:     0, Passed:    47, Skipped:     0, Total:    47 - SQCD.Agv.WireToGateG2Tests.dll (net8.0)
dotnet format --verify-no-changes：无改动
```

`6b21662` 之后，服务端只改了 G3 runner 的 commit 默认值（`a1243a8`）和证据，`src/`、`tests/` 没有改动。`98f4e06` 之后，车载端只增加了证据。

### 十三票本身的覆盖

十三票（#8 按判断不搬）以 cherry-pick 原样搬到 v2 线，测试随代码一起过来。每一票「能力 → 守它的测试」的对照在 v0.3.0 线那份报告第一节，这里不重抄。
搬过来之后，切片家族台账（`IntegrationSliceTraitArchitectureTests`）逐类登记过；`SlotConfigurationActivationTests` 与
`OnboardAlarmProjectionTests` 在消息面补齐后，分别移进了 `FP-IS-14` 和 `FP-IS-15`。

### v2 线上新增的能力与覆盖

| 能力 | 守它的测试 |
| --- | --- |
| 消息 7／8 的线上形状：命令 `RELIABLE` 下发、`SLOT_CONFIGURATION` 恢复角色、结果补报收敛 | `SlotConfigurationActivationWireTests`（6 条） |
| 消息 9 `OnboardAlarmSnapshot`：按 `(会话代, 序号)` 采纳，车重启后序号回到 1 仍被采纳 | `OnboardAlarmSnapshotWireTests`（5 条） |
| 消费 `CapabilitySnapshot.activeSlotConfigurationFingerprint`：不符时车降为不就绪，但仍可下发激活；车还没上报不算不符 | `CapabilitySnapshotFingerprintTests`（5 条） |
| 指纹不符的原因码能原样上线（不被映射表拦下） | `SessionReadinessReasonCodesTests.AFingerprintMismatchGoesOnTheWireAsItself` |
| 两端用同一个规范化摘要算指纹 | `SlotConfigurationActivationTests.TheCanonicalFingerprintOfTheSharedExampleIsTheValueTheOnboardSideAlsoComputes` |
| 激活发起入口：未配凭据 503、凭据错 401、字段错 422、无会话 409、成功 202、车离线也 202 | `SlotConfigurationActivationEndpointsTests`（6 条） |
| 看板判断在线，要能收到这一代会话的消息：断线的车显示失联，不显示失联前的告警 | `OnboardAlarmProjectionTests.AReadyRowWhoseSessionHasGoneQuietShowsTheReasonRatherThanItsLastKnownAlarms` |
| 看板集中显示一台车的全部告警，分类只回答它与车的关系（REQ-0270） | `OnboardAlarmProjectionTests.EveryAlarmOfAVehicleReachesTheDashboardWhateverItsRelationToTheVehicle` 与 `OnboardAlarmSnapshotWireTests` 的路由测试 |
| REQ-0271 审计导出：两条流分开、CSV（RFC 4180，带 BOM）与 JSON、时间窗左闭右开、不截断不删除 | `AuditExportTests`（6 条） |
| **车载端**告警接上真实来源：9 条条件的求值规则，光幕被挡不报，旧任务网关只在旧模式下判断 | `OnboardAlarmEvaluatorTests`（15 条） |
| **车载端**会话中途告警变化即报全量；握手完成前不插发；与服务端已有内容相同时不重报 | `WireToGateG2Tests` 的两条 `FP-IS-15` 测试 |
| 两端身份绑定已发布的协议；身份声称 `APPROVED_RELEASE` 时 gate runner 要求 tag 存在 | 两端 `ProtocolIdentityArchitectureTests`；`run-staged-g3.ps1` 与 `run-w2g-g2.ps1` 的 tag 检查 |

车载端 #27／#28 搬到 v2 线并接上消息 7／8／9，覆盖都在上面几套车载端测试里；两个切片的车载端证据见第三节 `ONBOARD_HMI_G2`。

## 二、L2

规格要求「新场景在 CI 上连续三次通过」。`l2.yml` 的 push 触发器只监听 `ControlServer_MVP` 与 `main`，所以用 `workflow_dispatch` 在 `fp/b3-on-v2` 上运行。

| 场景 | 讲什么 | 装置 |
| --- | --- | --- |
| `slot-configuration-activation-replay` | `FP-IS-14`：激活「下发 → 断线 → 重连 → 补报」。断线期间服务端不猜测，重连后补发同一行命令，车只报一次结果，服务端只收敛一次；顺带经 `FieldOps export-audit` 导出这次激活的业务审计 | 合成车载端，13 条判据 |
| `onboard-alarm-snapshot-dashboard` | `FP-IS-15`：「车载产生快照 → 服务端消费 → 看板可见」。断言读取真看板进程渲染出的页面；覆盖看板显示全部告警、整体取代、失联直述、重连采纳 | 合成车载端 ＋ 真看板进程，8 条判据 |

| 运行 | commit | 结果 | 证据 |
| --- | --- | --- | --- |
| 本地 | `e3c7c68`＋工作树 | 激活 PASS；**告警 FAIL（L2-OAS-07，失联直述）** | `evidence/l2/20260910-batch3-*-001` |
| 本地 | `eefb3a8` | 两个都 PASS | `evidence/l2/20260910-batch3-*-002` |
| CI `34461279984` | `b38b9ab` | 两个批次 3 场景各 3/3 PASS；**job failure**：批次 2 的 `load-result-requires-recovery` 红 | `evidence/l2/20260910-ci-34461279984-*` |
| CI `34463597736` | `aed3258` | 23/23 PASS；协议 `f6ee75d`（已被取代） | `evidence/l2/20260910-ci-34463597736-*` |
| CI `34690440914` | `17cf478` | 23/23 PASS；协议 `16e2567`（已被取代） | `evidence/l2/20260912-ci-34690440914-*` |
| **CI `34701119449`** | **`a1243a8`** | **23/23 PASS，job success**；两个批次 3 场景各 3/3；协议 `protocol-v1.0.0@9f22db8`，`APPROVED_RELEASE` | `evidence/l2/20260912-ci-34701119449-*` |

两次红都有结论：告警场景第一次运行抓到一个真实的产品缺陷（车断线后看板仍显示旧告警），修在 `eefb3a8`；CI 第一次那条红，是批次 2 既有场景的取样竞态，
与本批次改动无关，修在 `aed3258`。缺陷单见第四节。

合成车载端能证明的是服务端这一半。它没有 IO，直接采纳激活下发的目标指纹；「两端算出同一个指纹」要靠 G3 对真车载端来证明。
合成车载端能发出任意告警，所以「看板上看得到多种 scope 的非空告警」在 L2 这一层已经证明；真车载端在 G3 里报出的非空告警只有两种，见第三节。

## 三、门禁

**全部门禁绑同一组身份**：

- 服务端：`6b21662`
- 车载端：`98f4e06`（ONBOARD_HMI_G2）；`c86bac5`（G3，是在 `98f4e06` 之上只多了 G2 证据）
- 协议：`protocol-v1.0.0@9f22db8`，manifest `a0e1deed…`，`APPROVED_RELEASE`

三个 G3 runner 都没有带 `-Slice`，各自认领的切片各写一份 `gate-result.json`，并记录 `tagExists: true`。

### `FP-IS-14`

| 门禁 | 结果 | 绑定 | 证据 |
| --- | --- | --- | --- |
| `G1` | 候选 G1 与发布 G1 均 `PASS` | 协议 `9f22db8`，manifest `a0e1deed…` | `evidence/g1/20260912-protocol-v1.0.0-release-9f22db8` |
| `CONTROL_SERVER_G2` | `PASS`，25 条 | `6b21662` | `evidence/g2/20260912-protocol-v1.0.0-6b21662/FP-IS-14` |
| `ONBOARD_HMI_G2` | `PASS`，2 条，脚本内 G1 `PASS` | 车载端 `98f4e06` | 车载端仓 `evidence/g2/20260912-protocol-v1.0.0-98f4e06/FP-IS-14` |
| `G3` 主 runner | `PASS`，30 条 | `6b21662`，harness `a1243a8` | `evidence/g3/20260912-protocol-v1.0.0-staged-harness-a1243a8` |
| `G3` 进程重启（拒绝路径） | `PASS`，25 条（篡改后第二次激活被拒、生效配置不动） | `6b21662`，runner `22f0852b`（即 `a1243a8` 加证据） | `evidence/g3/20260912-protocol-v1.0.0-restart-harness-a1243a8` |

### `FP-IS-15`

| 门禁 | 结果 | 绑定 | 证据 |
| --- | --- | --- | --- |
| `G1` | `PASS` | 协议 `9f22db8` | 同上 |
| `CONTROL_SERVER_G2` | `PASS`，12 条 | `6b21662` | `evidence/g2/20260912-protocol-v1.0.0-6b21662/FP-IS-15` |
| `ONBOARD_HMI_G2` | `PASS`，3 条，脚本内 G1 `PASS` | 车载端 `98f4e06` | 车载端仓 `evidence/g2/20260912-protocol-v1.0.0-98f4e06/FP-IS-15` |
| `G3` 主 runner | `PASS`，30 条 | `6b21662`，harness `a1243a8` | `evidence/g3/20260912-protocol-v1.0.0-staged-harness-a1243a8` |
| `G3` 进程重启（车重启后告警采纳） | `PASS`，25 条（车重启后快照被采纳、之后不回退） | `6b21662`，runner `22f0852b` | `evidence/g3/20260912-protocol-v1.0.0-restart-harness-a1243a8` |

### `FP-IS-00`～`07`：票 17 的重证

这八片不是批次 3 的切片，但它们的证据同样绑着旧 manifest，所以同日在发布身份上一并重跑，让每一份现行证据都指向同一个已发布 commit。

| 门禁 | 结果 | 绑定 | 证据 |
| --- | --- | --- | --- |
| `CONTROL_SERVER_G2` 八片 | 全部 `PASS`（59／69／18／27／10／12／38／19 条） | `6b21662` | `evidence/g2/20260912-protocol-v1.0.0-6b21662` |
| `ONBOARD_HMI_G2` 八片 | 全部 `PASS`（11／5／5／9／2／5／8／19 条，脚本内 G1 均 `PASS`） | 车载端 `98f4e06` | 车载端仓 `evidence/g2/20260912-protocol-v1.0.0-98f4e06` |
| `G3` 需求线路（`FP-IS-04`／`05`） | `PASS`，20 条；现场库跑完后没有变化 | `6b21662` | `evidence/g3/20260912-protocol-v1.0.0-demand-bearing-harness-a1243a8` |
| `G3` 主 runner 与进程重启（`FP-IS-00`／`06`） | `PASS` | `6b21662` | 与上面两个切片同一轮 |

`FP-IS-01`／`02`／`03`／`07` 在任何 G3 runner 里都没有断言，按 2026-09-09 的裁定不出 G3 证据。

### G3 里真车载端报出的告警

`a98679f` 之前，真车载端报上去的告警快照永远是空的。现在：

- **主 runner**：看板投影最终为 1 条，`ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE`。staged 环境里出发安全信号是 `DISABLED`，读数一过期这条告警就会抬起。
  它是在续传连接存续期间抬起的，车载端就在那条连接上报了一份新的全量；下一次完整握手又报了一份，内容相同。
  这个时序在 `16e2567` 那一轮的首跑里让续传断言判了红，见第四节 R-5；`58f1a49` 修正断言之后，三轮都判 PASS。
- **进程重启 runner**：结束时投影里是 2 条，多出一条 `ONBOARD_SLOT_CONFIGURATION_MISMATCH`。phase 2 篡改车上生效配置之后，会话原因码带上了指纹不符，车载端据此抬起这条告警。
  车重启后，告警板序号从 1 重新开始仍被采纳；但观测点上的序号已经是 3（同一代里又报了两份），所以「序号 1 被采纳」是由会话代推出来的，不是直接观测到的。

## 四、红证据与缺陷单

红证据一份没删、没改。

| 证据 | 红在哪 | 记在哪 |
| --- | --- | --- |
| `evidence/g3/20260910-fp-is-15-v2-alarm-snapshot` | runner 断言假定每个连接都发告警快照 | `docs/defects/20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md` R-1 |
| `evidence/g3/20260910-fp-is-14-pending-result-replay`（`FP-IS-15` 红） | runner 断言假定整个 run 只有一次完整握手 | 同上 R-2 |
| `evidence/g3/20260910-fp-is-14-fingerprint-mismatch` | runner 篡改点选错 | 同上 R-3 |
| `evidence/g3/20260910-fp-is-14-15-activation-and-alarm`（INCONCLUSIVE） | 机器内存不足 | 同上 R-4（2026-09-12 已修进 runner） |
| `evidence/g3/20260910-fp-is-14-fingerprint-mismatch-corrected` | **产品**：握手时指纹不符就拒绝会话，与激活形成死锁；车离线时下发返回 500 | `docs/defects/20260910-fingerprint-mismatch-locked-the-vehicle-out-of-its-own-repair.md` D-1、D-2 |
| `evidence/g3/20260910-fp-is-14-fingerprint-mismatch-unready` | **产品**：新原因码没有协议映射，断连循环 | 同上 D-3（D-4 为自查发现，无红证据） |
| `evidence/l2/20260910-batch3-onboard-alarm-snapshot-dashboard-001` | **产品**：车断线后看板仍显示失联前的告警 | `docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md` |
| `evidence/l2/20260910-ci-34461279984-load-result-requires-recovery-01` | 批次 2 既有场景的取样竞态 | `docs/defects/20260910-l2-load-result-probe-read-the-stage-before-it-was-written.md` |
| 车载端仓 `…/FP-IS-14/…/20260910T041719210Z-d9ac1a18837a` | 装置：协议仓以 linked worktree 提供，G1 前置守卫假失败 | 该目录同级 `SUMMARY.md`「被纠正的那一份」 |
| `evidence/g3/20260912-fp-is-14-15-staged-6369616`（`FP-IS-15` 红） | runner 续传断言假定告警在会话中途不会变化；实际上告警内容真的变了 | `docs/defects/20260912-g3-resume-assertion-assumed-alarms-never-change-mid-session.md` R-5，修在 `58f1a49` |
| 车载端仓 `evidence/g2/20260912-fp-is-14-15-single-owner-e30d421$s` 下两份 | 门禁脚本把参数数组整个传给 `pnpm.ps1`，协议依赖装不上，G1 没有运行；车载端测试与 format 当时都通过 | 车载端仓 `ad0e507` 的提交信息，以及那一轮现行 `SUMMARY.md` 的「被纠正的那两份」 |

另有几处不是红、但需要知道的记录，都原样保留：

- `evidence/g2/20260910-fp-is-14-15-v2-message-plane-6dc4bc8$s` 与 `evidence/g2/20260912-fp-is-14-15-single-owner-6369616$s`：
  调用笔误产出的 `PASS`，原因相同（Bash 命令里的路径转义被多解码了一层），说明在各自同日 G2 的 `SUMMARY.md` 里。之后按切片跑门禁一律改用 pwsh 脚本文件。
- `evidence/g3/20260912-fp-is-14-15-restart-6369616`：四片 `PASS`，但 `runnerWorktreeCleanAtStart: false`，不作为现行证据。
- **发布的第一次尝试**：发布 G1 已经 PASS，但全新克隆里没有 git 身份，打 tag 时报 `Committer identity unknown`，脚本停止。tag 没有打出，也没有发布。
  那份没有用上的 attestation 与日志保存在 `evidence/g1/20260912-protocol-v1.0.0-release-9f22db8/`。
- **`16e2567` 身份上那一整轮 PASS**（`*-single-owner-*`、`*-harness-58f1a49`、`20260912-fp-is-04-05-demand-bearing-6369616`、CI `34690440914`）：
  对它们各自的绑定仍然成立，但 `9f22db8` 改了批准规则之后，不再是现行证据。

## 五、需求覆盖，以及明确没做的

批次 3 各票承载的需求：#9 REQ-0266／0271／0320／0346；#10 REQ-0257／0258／0260／0261／0267；#11 REQ-0310／0311／0316～0320；
#12 REQ-0268／0269／0270；#13 REQ-0259／0262／0263；#14 REQ-0326／0339／0346；#15 REQ-0264／0265／0316；#16 REQ-0269／0270；
#17 REQ-0259／0263；车载端 #27 REQ-0264；#28 REQ-0269／0270／0272。

没做完的，逐条写清：

| 项 | 现状 | 性质 |
| --- | --- | --- |
| REQ-0339 后半：「新鲜二次认证」 | 零实现，有架构测试守着「没有人拿共享密钥顶上」 | **按规格延后**（随 `FP-C10`），不是遗漏 |
| REQ-0271 后半：超过 180 天的保留／归档／清理由管理员配置，**变更本身要留管理员审计** | 2026-09-12 已做：服务启动时比对保留期，与上一次记下的不同（或从未记过）就写一条 `AUDIT_RETENTION_POLICY_CONFIGURED` 管理员审计，带前后两个值 | 已做 |
| REQ-0270 看板侧：全部告警集中显示 | 2026-09-12 按原文改为显示一台车的全部告警（`5f7a34e`） | 已做，G2／L2／G3 证过 |
| REQ-0270 车载侧：真车产生告警 | 2026-09-12 接上 9 条条件（`a98679f`）：IO 离线、锁反馈丢失、旧任务网关断开（仅旧模式）、作业未完成（v2）／超时（旧模式）、控制器故障锁定、出发安全信号不可用、仓门状态陈旧、行驶中仓门未锁或开锁输出未复位、仓位配置指纹不符。光幕被挡不报。本机状态区显示与本车、当前作业相关的告警 | 已做；**真进程之间只出现过「出发安全信号不可用」「仓位配置指纹不符」两条**，其余条件只在车载端单元测试里证过，没有在现场 IO 下验证 |
| REQ-0269：车队会话卡片的失联直述 | 与告警卡片是同一类缺陷：断线的车在那张卡片上仍显示 `Ready`。2026-09-12 已修，两张卡片共用 `SessionLiveness` | 已修 |
| REQ-0259／0263：W1 现场核对与门禁逐台启用 | 代码与 FieldOps 工具就绪，现场没做。2026-09-12 定走 v2 线；**2026-09-13 改定**：在生产现有的 v0.3.0 库上做，不等生产切到 v2 线，真信号由产品负责人本人在车前逐仓核对 | 未做，见第七节 |
| `protocol-v1.0.0` 正式发布 | 2026-09-12 已发布：注释 tag 指向 `9f22db8`，Release 附带 `release-approval.json`（SHA-256 `545fba1c…`），发布 G1 PASS。批准人为 `AI_AGENT`，由产品负责人授权。治理规则、attestation schema 与候选生成器同日修改（生成器 `8a5dfd7d`），重新生成的结果与 `9f22db8` 逐字节相同 | 已发布 |
| 被篡改过配置的车怎么恢复 | 2026-09-12 定口径：把车上的生效配置改回已批准那一版，服务端不迁就。规程 `docs/field/tampered-slot-configuration-recovery.md`；这条路径**还没有门禁证过** | 规程已写，未证 |
| runner OOM（R-4） | 2026-09-12 修进两个 G3 runner：脚本开头关闭 MSBuild node 复用，publish 之后记录一条 `build-server-shutdown` | 已修 |

## 六、两处表述纪律

**`FP-IS-14`／`FP-IS-15` 是新切片，不存在可沿用的通过结论。**本报告里每一个 `PASS` 都有本批次自己的证据目录和精确的 commit 绑定；没有任何一条引自
v0.3.0 线或更早的切片，也没有任何一条引自 `protocol-v1.0.0` 发布之前的候选身份。`W2G-IS-00`～`07` 与 RC 仍是 `INCONCLUSIVE`。

**完整产品的验收证据里没有任何人员认证项**（`FP-C10`、`FP-C6` 整簇延后）。批次 0 的权限骨架两端零实现，唯一的「认证」是一个全场共用的环境变量；
激活入口与恢复入口用的都是这一类共享凭据，它们证明「持有凭据」，不证明「是谁」。

## 七、结账

| 待办 | 卡在 |
| --- | --- |
| W1 现场核对（#17） | **2026-09-13 产品负责人定了做法，原来的 ①②③④ 不再是前置**：W1 只动数据库、与协议版本无关，所以在生产现有的 v0.3.0 库上做（生产 `3b379bb` 已含批次 3 的表与 FieldOps），不出 v2 的包、不切生产；逐仓核对按真信号、本人在车前做，不走模拟器（REQ-0263 不允许模拟结果冒充现场通过；v0.3.0 线上车载端也没有不经服务端授权的开锁路径，且只服务一台车）。工具在 `ControlServer_MVP` 线 `feat/w1-unattended`：逐仓 IO 探针在车上直读 IO 模块，已在控制端对模拟器彩排、对 agv01 真模块只读自检。**还卡在**：agv02／agv03 2026-09-13 从厂区服务器直连 22022 与 5001 都不通，要现场通电；已批准硬件事实里光幕极性写成 `ACTIVE_HIGH`、与票据 35 不符（`ControlServer_MVP` 线缺陷单 `20260913-approved-slot-facts-call-the-light-curtain-active-high.md`），在生产库上 seed 之前要定处置。生产从 v0.3.0 切到 v2 另立项 |
| 规格 6.6 第 6 条的批准人表述 | 规格原文仍写两名产品负责人；协议治理已先后改为一名、再到可由授权 AI 批准。规格在 `8005-agv-program`，要不要跟着改由产品负责人决定 |
| 篡改车恢复规程的证据 | 给 `run-staged-g3-restart.ps1` 加恢复阶段，进门禁需要批准 |
| 批次 3 分支合入集成分支 | **已合入，不再卡。**首轮：服务端 #32（`59c27b2`）、车载端 #40（`e83804a`）。发布之后的跟进轮：服务端 #37（merge commit `084d8678`，2026-09-12 23:59）、车载端 #43（merge commit `d2a09fce`，2026-09-13 00:05），经产品负责人同意，均用 merge commit 合并，证据绑定的 `27be8ba1`／`c86bac5` 原样保留在历史里。`OnboardHmi_MVP` 合并前后都是 `f840d84`，未动 |
| GitHub 上 #8～#18 与车载端 #27／#28 的票面状态 | 本报告落定后逐票更新 |
