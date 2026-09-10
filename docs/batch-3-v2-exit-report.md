# 批次 3 出口报告（v2 线）：治理面单端工程

日期：2026-09-10
服务端集成分支：`fp/b3-on-v2`（从 `fp/v2-impl` 的 `1f25f6e` 开出）
车载端分支：`8005-agv-onboard-hmi` 的 `w2g/b3-on-v2`
协议：`8005-agv-protocol` 的 `fp/v2-candidate@f6ee75d`（v2 候选，`protocol-v1.0.0` 尚未打 tag）

**这是批次 3 在 v2 线上的账。**v0.3.0 线（`ControlServer_MVP`）上另有一份 `docs/batch-3-exit-report.md`，结论是
「尚未出口」，原因是十三票做在 v0.3.0 线、切片与 v2 两端实现在另一条线上、两条线没有汇合。本报告记的就是汇合之后：
十三票搬到 v2 线、补齐协议 v2 消息面、跑完门禁与 L2 的结果。两份报告各记各的线，不互相替代。

## 结论

**默认三条出口与两个切片的四道门禁在 v2 线上全部成立；W1 现场逐车逐仓 IO 核对没有做，所以按规格 8.3，批次 3 还没有出口。**

| 出口（规格 8.2／8.3、#18） | 状态 | 依据 |
| --- | --- | --- |
| L1 绿，新能力有新增覆盖 | **成立** | 服务端 697 passed（`eefb3a8`）；车载端 185 ＋ 45 passed（`afba86e`）。逐项见第一节 |
| L2 新场景在 CI 上连续三次通过，三份证据各自独立 | **成立** | CI run `34463597736`（`aed3258`）整条 `success`，两个批次 3 场景各 3/3。见第二节 |
| `assertions.json` 的 `identity` 含 `protocolReleaseIdentity` 与 `batchId` | **成立** | CI 证据里 `batchId` 为 `batch-3`，`protocolReleaseIdentity` 从 `/version` 实读 |
| `FP-IS-14` 四门禁全 PASS，与 `ProtocolReleaseIdentity` 精确绑定 | **成立** | 见第三节 |
| `FP-IS-15` 四门禁全 PASS，与 `ProtocolReleaseIdentity` 精确绑定 | **成立** | 见第三节 |
| 每次门禁 `-Output`／`-EvidenceRoot` 都是新目录，无覆盖 | **成立** | 所有纠正都写新目录，`SUMMARY.md` 指回被纠正的那份 |
| 红证据全部保留，失败原因在 `docs/defects/` 有 `Found by:` | **成立** | 四份缺陷单，见第四节 |
| **W1：三台车逐台逐仓 IO 核对、逐台放行（#17）** | **未做** | 现场作业，且要先定部署哪条线，见第六节 |

两点保留，不影响上表但必须读到：

- **协议 v2 还是候选。**所有门禁绑的是协议 commit `f6ee75d`，`protocolApprovalStatus` 为 `SUPERSEDING_CANDIDATE`，
  `tagExists: false`。正式发布要 attestation 加产品负责人本人签名，AI 与 CI 不能批准。签名后在 `f6ee75d` 上打 tag，
  证据里的三元组即与 tag 一致；runner 在 tag 存在却指向别的 commit 时直接 throw，所以不会出现「证据与 tag 对不上」而没人发现。
- **整体 G3 仍是 `INCONCLUSIVE`。**两个 G3 runner 认领 `FP-IS-00`／`06`／`14`／`15`，`FP-IS-01`／`02`／`03`／`07` 本批次没有
  可测的面。本批次要的是两个切片的门禁，不是整体 G3。

## 一、L1

### 实测

```
服务端（fp/b3-on-v2@eefb3a8）
Passed!  - Failed:     0, Passed:   697, Skipped:     0, Total:   697 - ControlServer.Tests.dll (net8.0)

车载端（w2g/b3-on-v2@afba86e）
Passed!  - Failed:     0, Passed:   185, Skipped:     0, Total:   185 - SQCD.Agv.UnitTests.dll (net8.0)
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45 - SQCD.Agv.WireToGateG2Tests.dll (net8.0)
```

`eefb3a8` 之后服务端只改了 L2 场景脚本与证据，`src/`、`tests/` 没动。

### 十三票本身的覆盖

十三票（#8 除外，它按判断不搬）以 cherry-pick 原样搬到 v2 线，测试随代码一起过来。逐票「能力 → 守它的测试」
对照在 v0.3.0 线那份报告第一节，这里不重抄。搬过来之后切片家族台账（`IntegrationSliceTraitArchitectureTests`）
逐类登记过；`SlotConfigurationActivationTests` 与 `OnboardAlarmProjectionTests` 在消息面补齐后移进了
`FP-IS-14`／`FP-IS-15`。

### v2 线上新增的能力与覆盖

| 能力 | 守它的测试 |
| --- | --- |
| 消息 7／8 的线上形状：命令 `RELIABLE` 下发、`SLOT_CONFIGURATION` 恢复角色、结果补报收敛 | `SlotConfigurationActivationWireTests`（6 条） |
| 消息 9 `OnboardAlarmSnapshot`：按 `(会话代, 序号)` 采纳，车重启序号回到 1 仍被采纳 | `OnboardAlarmSnapshotWireTests`（5 条） |
| `CapabilitySnapshot.activeSlotConfigurationFingerprint` 被消费；不符时车降为不就绪但仍可下发激活；车还没报不算不符 | `CapabilitySnapshotFingerprintTests`（5 条） |
| 指纹不符的原因码能原样上线（不被映射表拦下） | `SessionReadinessReasonCodesTests.AFingerprintMismatchGoesOnTheWireAsItself` |
| 两端用同一个规范化摘要算指纹 | `SlotConfigurationActivationTests.TheCanonicalFingerprintOfTheSharedExampleIsTheValueTheOnboardSideAlsoComputes` |
| 激活发起入口：未配凭据 503、凭据错 401、字段错 422、无会话 409、成功 202、车离线也 202 | `SlotConfigurationActivationEndpointsTests`（6 条） |
| 看板判在线要听得到这一代会话：断线的车显示失联，不显示失联前的告警 | `OnboardAlarmProjectionTests.AReadyRowWhoseSessionHasGoneQuietShowsTheReasonRatherThanItsLastKnownAlarms` |
| REQ-0271 审计导出：两条流分开、CSV（RFC 4180，带 BOM）与 JSON、窗口左闭右开、不截断不删 | `AuditExportTests`（6 条） |

车载端 #27／#28 搬到 v2 线并接上消息 7／8／9，覆盖在上面两套车载端测试里；两个切片的车载端证据见第三节
`ONBOARD_HMI_G2`。

## 二、L2

规格要求「新场景在 CI 上连续三次通过」。`l2.yml` 的 push 触发器只盯 `ControlServer_MVP` 与 `main`，所以用
`workflow_dispatch` 在 `fp/b3-on-v2` 上跑。

| 场景 | 讲什么 | 装置 |
| --- | --- | --- |
| `slot-configuration-activation-replay` | `FP-IS-14`：激活「下发 → 断线 → 重连 → 补报」。断线期间服务端不猜，重连后补发同一行命令，车只报一次结果，只收敛一次；顺带经 `FieldOps export-audit` 导出这次激活的业务审计 | 合成车载端，13 条判据 |
| `onboard-alarm-snapshot-dashboard` | `FP-IS-15`：「车载产快照 → 服务端消费 → 看板可见」。断言读真看板进程渲染的页面；收敛规则、整体取代、失联直述、重连采纳 | 合成车载端 ＋ 真看板进程，8 条判据 |

| 运行 | commit | 结果 | 证据 |
| --- | --- | --- | --- |
| 本地 | `e3c7c68`＋工作树 | 激活 PASS；**告警 FAIL（L2-OAS-07，失联直述）** | `evidence/l2/20260910-batch3-*-001` |
| 本地 | `eefb3a8` | 两个都 PASS | `evidence/l2/20260910-batch3-*-002` |
| CI `34461279984` | `b38b9ab` | 两个批次 3 场景各 3/3 PASS；**job failure**：批次 2 的 `load-result-requires-recovery` 红 | `evidence/l2/20260910-ci-34461279984-*` |
| **CI `34463597736`** | **`aed3258`** | **23/23 PASS，job success**；两个批次 3 场景各 3/3 | `evidence/l2/20260910-ci-34463597736-*` |

两次红都有结论：告警场景第一次跑抓到一个真产品缺陷（车断线后看板仍显示旧告警），修在 `eefb3a8`；CI 第一次那条红
是批次 2 既有场景的取样竞态，与本批次改动无关，修在 `aed3258`。缺陷单见第四节。

合成车载端能证的是服务端这一半。它没有 IO，采纳激活的目标指纹；「两端算出同一个指纹」由 G3 对真车载端证。
它能发任意告警，所以「看板上看得到非空告警」在 L2 这一层证到了——真车载端做不到，见第五节。

## 三、门禁

### `FP-IS-14`

| 门禁 | 结果 | 绑定 | 证据 |
| --- | --- | --- | --- |
| `G1` | `PASS` | 协议 `f6ee75d`，manifest `84f984ea…` | `evidence/g1/20260910-fp-is-14-15-protocol-g1` |
| `CONTROL_SERVER_G2` | `PASS`，25 条 | `6dc4bc8` | `evidence/g2/20260910-fp-is-14-15-v2-message-plane-6dc4bc8/FP-IS-14` |
| `ONBOARD_HMI_G2` | `PASS` | 车载端 `d9ac1a1` | 车载端仓 `evidence/g2/20260910-fp-is-14-15-v2-message-plane/FP-IS-14/…/20260910T042025069Z-d9ac1a18837a` |
| `G3` 主 runner | `PASS`，30 条 | `6dc4bc8` | `evidence/g3/20260910-fp-is-14-15-staged-6dc4bc8` |
| `G3` 进程重启（拒绝路径） | `PASS`，25 条 | `6dc4bc8` | `evidence/g3/20260910-fp-is-14-fingerprint-mismatch-mapped` |

### `FP-IS-15`

| 门禁 | 结果 | 绑定 | 证据 |
| --- | --- | --- | --- |
| `G1` | `PASS` | 协议 `f6ee75d` | 同上 |
| `CONTROL_SERVER_G2` | `PASS`，12 条 | `eefb3a8` | `evidence/g2/20260910-fp-is-15-alarm-liveness-eefb3a8` |
| `ONBOARD_HMI_G2` | `PASS` | 车载端 `d9ac1a1` | 车载端仓 `evidence/g2/20260910-fp-is-14-15-v2-message-plane/FP-IS-15/…/20260910T042141809Z-d9ac1a18837a` |
| `G3` 主 runner | `PASS`，30 条 | `eefb3a8` | `evidence/g3/20260910-fp-is-15-staged-eefb3a8` |
| `G3` 进程重启（车重启后告警采纳） | `PASS`，25 条 | `eefb3a8` | `evidence/g3/20260910-fp-is-15-onboard-restart-eefb3a8` |

### 为什么两个切片绑的服务端 commit 不同

`eefb3a8` 修的是 `FP-IS-15` 的看板失联判定，`6dc4bc8..eefb3a8` 之间服务端 `src/` 只多了两处：`OnboardAlarmProjectionStore`
（`FP-IS-15`）与 `AuditExport`（REQ-0271，不属于任何切片）。`FP-IS-14` 的代码与测试没有动，它的四道门禁一致绑在
`6dc4bc8`；`FP-IS-15` 在 `eefb3a8` 上重跑了服务端那三道，一致绑在 `eefb3a8`。重跑两个 G3 时用 `-Slice FP-IS-15`，
只为这一片写 `gate-result.json`，没有让 `FP-IS-14` 在两个 commit 上各有一份。

车载端在 `d9ac1a1` 之后只增加了证据文件，两份 `ONBOARD_HMI_G2` 仍绑定现行车载端代码。

## 四、红证据与缺陷单

红证据一份没删、没改。

| 证据 | 红在哪 | 记在哪 |
| --- | --- | --- |
| `evidence/g3/20260910-fp-is-15-v2-alarm-snapshot` | runner 断言假定每个连接都发告警快照 | `docs/defects/20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md` R-1 |
| `evidence/g3/20260910-fp-is-14-pending-result-replay`（`FP-IS-15` 红） | runner 断言假定整个 run 只有一次完整握手 | 同上 R-2 |
| `evidence/g3/20260910-fp-is-14-fingerprint-mismatch` | runner 篡改点选错 | 同上 R-3 |
| `evidence/g3/20260910-fp-is-14-15-activation-and-alarm`（INCONCLUSIVE） | 机器内存不足 | 同上 R-4（只是缓解，runner 内未修） |
| `evidence/g3/20260910-fp-is-14-fingerprint-mismatch-corrected` | **产品**：握手时指纹不符拒绝会话，与激活死锁；离线下发回 500 | `docs/defects/20260910-fingerprint-mismatch-locked-the-vehicle-out-of-its-own-repair.md` D-1、D-2 |
| `evidence/g3/20260910-fp-is-14-fingerprint-mismatch-unready` | **产品**：新原因码无协议映射，断连循环 | 同上 D-3（D-4 为自查发现，无红证据） |
| `evidence/l2/20260910-batch3-onboard-alarm-snapshot-dashboard-001` | **产品**：车断线后看板仍显示失联前的告警 | `docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md` |
| `evidence/l2/20260910-ci-34461279984-load-result-requires-recovery-01` | 批次 2 既有场景的取样竞态 | `docs/defects/20260910-l2-load-result-probe-read-the-stage-before-it-was-written.md` |
| 车载端仓 `…/FP-IS-14/…/20260910T041719210Z-d9ac1a18837a` | 装置：协议仓以 linked worktree 提供，G1 前置守卫假失败 | 该目录同级 `SUMMARY.md`「被纠正的那一份」 |

另有一个 `evidence/g2/20260910-fp-is-14-15-v2-message-plane-6dc4bc8$s` 目录，是一次调用笔误产出的 `PASS`，不是红，
原样保留，说明在同日 G2 的 `SUMMARY.md` 里。

## 五、需求覆盖，以及明确没做的

批次 3 各票承载的需求：#9 REQ-0266／0271／0320／0346；#10 REQ-0257／0258／0260／0261／0267；#11 REQ-0310／0311／0316～0320；
#12 REQ-0268／0269／0270；#13 REQ-0259／0262／0263；#14 REQ-0326／0339／0346；#15 REQ-0264／0265／0316；#16 REQ-0269／0270；
#17 REQ-0259／0263；车载端 #27 REQ-0264；#28 REQ-0269／0270／0272。

没做完的，逐条写清：

| 项 | 现状 | 性质 |
| --- | --- | --- |
| REQ-0339 后半：「新鲜二次认证」 | 零实现，有架构测试守着「没有人拿共享密钥顶上」 | **按规格延后**（随 `FP-C10`），不是遗漏 |
| REQ-0271 后半：超过 180 天的保留／归档／清理由管理员配置，**变更本身要留管理员审计** | 保留期是启动配置，改它不产生审计记录 | 未做 |
| REQ-0270 车载侧：真车产生告警 | 车载端 `OnboardAlarmBoard.Raise` 在产品代码里没有调用者，真车报的告警快照永远是空的；#28「与当前 AGV／停靠／操作相关的告警显示在本机界面」在真车上触发不了 | 未做，**缺的是「什么条件算一条告警」的定义** |
| REQ-0269：车队会话卡片的失联直述 | 与告警卡片同一类缺陷：断线的车在那张卡片上仍显示 `Ready` | 未修，记在告警卡片缺陷单「仍然开着的」 |
| REQ-0259／0263：W1 现场核对与门禁逐台启用 | 代码与 FieldOps 工具就绪，现场没做 | 未做，**要先定部署哪条线** |
| 被篡改过配置的车怎么恢复 | 服务端正确拒绝、车降为不就绪但在线；「修回来」这条路没有证 | 未证，**要先定口径** |
| runner OOM（R-4） | 靠每次手工关构建服务器缓解 | 未修 |

## 六、两处表述纪律

**`FP-IS-14`／`FP-IS-15` 是新切片，不存在可沿用的通过结论。**本报告里每一个 `PASS` 都有本批次自己的证据目录与精确
commit 绑定；没有任何一条引自 v0.3.0 线或更早的切片。`W2G-IS-00`～`07` 与 RC 仍是 `INCONCLUSIVE`。

**完整产品的验收证据里没有任何人员认证项**（`FP-C10`、`FP-C6` 整簇延后）。批次 0 的权限骨架两端零实现，唯一的「认证」
是一个全场共用的环境变量；激活入口与恢复入口用的都是这一类共享凭据，它们证明「持有凭据」，不证明「是谁」。

## 七、结账

| 待办 | 卡在 |
| --- | --- |
| W1 现场核对（#17） | 部署哪条线（产品负责人决定）；选 v2 线则还要先签发 `protocol-v1.0.0`、出包、部署服务端与三台车；现场安全 GO |
| `protocol-v1.0.0` 正式发布 | attestation ＋ 产品负责人本人签名 |
| 车载告警来源 | 「哪些条件算一条告警」的定义 |
| 篡改车的恢复口径 | 产品负责人决定 |
| 车队会话卡片失联直述、REQ-0271 后半、runner OOM | 无外部阻塞，可直接排 |
| 批次 3 分支合入集成分支（`fp/b3-on-v2 → fp/v2-impl`、`w2g/b3-on-v2 → w2g/fp-v2-impl`） | 开 PR／合并需产品负责人同意 |
| GitHub 上 #8～#18 与车载端 #27／#28 的票面状态 | 本报告落定后逐票更新 |
