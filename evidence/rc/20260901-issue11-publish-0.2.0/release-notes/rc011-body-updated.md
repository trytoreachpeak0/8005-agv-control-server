> **后续版本提示（2026-09-01 追加，未改动本 release 的 tag 与资产）**
>
> 自 `w2g-mvp-rc-0.2.0` 起，OnboardHmi ↔ ControlServer 的两条链路改为**明文 TCP/HTTP**，整套证书机制已从产品代码、安装链与手册中移除。
>
> **0.1.1 与 0.2.0 的服务端／车载端不可互通**，且 0.1.x 的站点配置会被 0.2.0 **显式拒绝启动**——两端必须同版本升级。错配的失败形态不含 TLS 字样，极易误判成网络不通，对照表见 0.2.0 包内手册 §8.1。
>
> 本 release（0.1.1）保持不可变，继续适用于仍在 TLS 形态下运行的站点。新部署与从本版本升级请见 **[`w2g-mvp-rc-0.2.0`](https://github.com/trytoreachpeak0/8005-agv-control-server/releases/tag/w2g-mvp-rc-0.2.0)**，升级路径为其包内手册 §4.5。
>
> 明文形态有三条新引入的已知限制（凭据明文过网、安全闸门输入可篡改、健康与版本端点随投影一并暴露），适用前提是**受控的工厂内网**。升级前请先读 0.2.0 说明第 6 节。

WIRE_TO_GATE MVP Release Candidate 0.1.1。**受控测试环境可用，未取得工厂生产资格**——两条硬件资格仍未通过，见第 7 节。

本候选相对 `w2g-mvp-rc-0.1.0` 只改一件事：**车载端二进制从 `OnboardHmi_MVP@304e6ad` 换成 `@31263b1`**，收进 0.1.0 已知限制第 1a 项所说的那个 HMI 可见性修复。服务端产品代码是同一个 commit，协议身份逐字未变。

包内 `RELEASE-CANDIDATE.md` 仍是唯一操作入口。本说明只做包内文件做不到的事：版本绑定与资产哈希、本轮实际验证到哪一步、哪些结论是从 0.1.0 沿用的以及为什么、原样携带的已知限制，以及一条**包外**手册更正。

---

## 1. 版本绑定

| 组件 | 仓库 / 分支 | commit | 相对 0.1.0 |
| --- | --- | --- | --- |
| ControlServer | `8005-agv-control-server` / `ControlServer_MVP` | `9daeef4325fccf094767b689fa484d8e1e414042` | **未变**（0.1.0 的资产建自 `81cb9cf`，其后到 `9daeef4` 只有 `evidence/` 与 13 行文档，`src` 无提交） |
| OnboardHmi | `8005-agv-onboard-hmi` / `OnboardHmi_MVP` | `31263b1ffd372db1f27af5e1143ebad7e7679715` | **本候选唯一的变化**（0.1.0 是 `304e6ad`） |
| 协议 | `8005-agv-protocol` / tag `protocol-v0.1.1` | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | 未变 |

协议身份（`ProtocolReleaseIdentity`，`profileId=WIRE_TO_GATE_MVP`、`protocolVersion=1`、`approvalStatus=APPROVED_RELEASE`），从产物读回而非复述：

| 字段 | 值 | 与 0.1.0 |
| --- | --- | --- |
| `repositoryCommit` | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | 逐字相同 |
| `manifestSha256` | `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f` | 逐字相同 |
| `schemaBundleSha256` | `e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c` | 逐字相同 |
| `vectorsSha256` | `fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e` | 逐字相同 |

**服务端同 commit，但服务端程序集哈希仍与 0.1.0 不同。** 这不是内容差异：托管构建每次产生新 MVID，票 25 已用同源车载端程序集证明过这一点，故程序集哈希对内容什么都不能证明。本轮改用符号级判据，见第 4 节。

---

## 2. 资产与校验

| 资产 | 大小 | SHA-256 |
| --- | --- | --- |
| `w2g-rc-20260831-31263b1.zip` | 123,983,741 B | `727b3fbb9b0db9b82321b2e304564566634fa8d19583fdecf409797a87a364f0` |
| `release-manifest.json` | 171,956 B | `12a7ce56ff1f74eb6a15e15bc1947240876262c4d457c1b1e3573ce04912578b` |
| `SHA256SUMS.txt` | 98,898 B | `e607a9d9adedcf91e13b832e868d78273137e69af7098c8fd828c21c35384847` |

后两个文件与 zip 内根目录下的同名文件是同一份，单独上传只为不下载 118 MB 也能读到清单。zip 解压出单个顶层目录 `w2g-rc-20260831-31263b1/`，与 0.1.0 同为 868 个文件。

解压后在包根目录执行（手册第 3 节）：

```powershell
Get-Content .\SHA256SUMS.txt | ForEach-Object {
    $parts = $_ -split '  ', 2
    if ($parts.Count -eq 2) {
        $actual = (Get-FileHash -LiteralPath $parts[1] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $parts[0]) { "MISMATCH $($parts[1])" }
    }
}
```

无输出即 868 个文件全部一致。发布前在本机复验为 `OK=868 MISMATCH=0 MISSING=0`。该校验器**证明会红**：对 `controlserver\appsettings.json` 的副本追加一个字节，同一条比对由 GREEN 变 RED；产物本身未被修改，事后重新哈希仍等于清单值。

---

## 3. 与 0.1.0 逐文件的差异

| 组件 | 相同 | 变更 | 新增 | 删除 |
| --- | --- | --- | --- | --- |
| `controlserver/` | 373 | 10 | 0 | 0 |
| `onboard-hmi/` | 465 | 12 | 0 | 0 |

- `controlserver/` 的 10 项全部是托管程序集、其 pdb 与随之变化的 `deployment-manifest.json`——同一 commit 的重建噪声，非内容差异；
- `onboard-hmi/` 的 12 项是新车载端 commit 的程序集与 pdb，外加 `appsettings.Production.template.json`——构建脚本已把其中的 `onboardBuildCommit` 填成本包真实的 `31263b1…`。

---

## 4. 本轮实际验证了什么

以下全部针对**本候选**，不是沿用：

| 门禁 | 结果 |
| --- | --- |
| 两端构建警告 | ControlServer 0，OnboardHmi 0（脚本对 >0 直接 throw） |
| 发布扫描闸门 | 秘密 finding 0、密钥材料文件 0、UNRESOLVED 许可证仅三个具名自建 RIoT SDK 包 |
| 868 条哈希全量回验 | `OK=868 MISMATCH=0 MISSING=0`，并证明会红 |
| 修复在出厂字节里（元数据级） | 见下表 |
| 隔离安装 / 卸载 | 安装 PASS（9 项检查、迁移建库、NDJSON 落盘）；卸载 PASS（服务与目录移除、根证书移除 1 张、生产服务未受影响） |
| HMI 界面核对 | 见 4.2 |
| **现场端到端闭环（真车、真单、generation 8）** | **`Stage=Completed`**，见 4.3 |

### 4.1 修复确实进了二进制

`31263b1` 新增的四个符号写进了程序集元数据字符串堆：

| 符号 | 0.1.0 产物（`304e6ad`） | 本候选（`31263b1`） |
| --- | --- | --- |
| `ApplyWireToGatePresentationCore` | ABSENT | **PRESENT** |
| `ApplyWireToGateOperatorEvent` | ABSENT | **PRESENT** |
| `MatchesPersistedResumeState` | ABSENT | **PRESENT** |
| `WireToGateHmiBanner` | ABSENT | **PRESENT** |
| `HandleBlockedResumeAsync`（既有，对照） | PRESENT | PRESENT |
| `SubmitSublotAsync`（既有，对照） | PRESENT | PRESENT |

**红侧不是空的**：后两行用同一套检索、同一文件布局，在旧产物里确实找得到符号，因此前四行的 ABSENT 是真阴性而非坏读取。四个符号在 `304e6ad` 的源码里也确实不存在（`git grep` 命中 0 个文件），所以它们是新增而非改名。

### 4.2 HMI 界面：同一服务端状态下的红绿对照

在同一台隔离实例（`tls://127.0.0.1:58605` / `https://localhost:58607`）上，先后启动两份车载端，配置逐字相同（只差各自声明的 `onboardBuildCommit` 与 journal 路径），服务端两次都判定 `readiness=RecoveryRequired`：

| 界面元素 | 0.1.0 车载端（`304e6ad`） | 本候选（`31263b1`） |
| --- | --- | --- |
| 状态横幅 | 「连接中 — 正在等待仓门控制设备和任务系统连接…」 | 「**需要恢复** — 上层会话要求恢复：`DEPARTURE_SAFETY_NOT_READY`。请保持车辆停稳，禁止重复操作仓门。」 |
| 顶栏「任务系统」 | 离线 | **在线** |
| 顶栏「上层会话」 | 需恢复 | 需恢复（两侧一致） |

两侧日志都记录了 `上层会话已建立：generation=N，readiness=RecoveryRequired`，且 `version=…+<commit>` 分别落在各自的 commit 上——即会话状态相同、二进制不同，界面差异只能来自修复本身。这印证了 0.1.0 已知限制第 1a 项所述的病因：旧版横幅被恒为离线的 `DisabledRuleGateway` 钉死，新版改由正式上层会话驱动。

这一对照本身不含业务（两侧操作记录都只有启动两行）；操作记录投影由下一节的现场闭环补齐。

### 4.3 现场端到端闭环，在本候选上重跑通过（generation 8）

用户给出现场物理安全 GO，操作员 `S0020310`，`dispatchGeneration=8`，用票 14 的现成脚本把 `-ReleaseRoot` 指向本候选。

**终态 `Stage=Completed`、`BlockReasonCode` 为空、`SessionGeneration` 全程 1**，`08:43:15Z → 08:52:54Z`。Demand `18b7d18f…`，取货站 `N2-4_N3-4`（20），关卡 210，一筐。

两条真单各建一次，五步审计各自齐全，共 10 条：

| upperId | 用途 | 目标 | RIoT 订单 |
| --- | --- | --- | --- |
| `W2G-18b7d18f-…-PICKUP-8` | `TO_PICKUP` | `N2-4_N3-4` / 20 | `order-2094345282652340224` |
| `W2G-18b7d18f-…-GATE-8` | `TO_GATE` | `关卡` / 210 | `order-2094346185258172416` |

终态计数：`AcceptedDemands=1`、`JourneyRuntimes=1`、`OrderIntents=2`、`RiotDispatchAuditEvents=10`、`StationOperations=2`（`Load`／`Unload` 均 `Committed`，子批 `Q26085155-5`）、`OperationResults=2`、`ProtocolInbox=171`、`ProtocolOutbox=10`；`hostStderrEmpty=true`、端口全部回收、临时根证书移除后 `trustRemaining=0`。

**这一轮把 4.2 留下的操作记录缺口补上了。** 四张截图横跨全程，逐个节点都能看到 HMI 在跟随业务——这是 `304e6ad` 做不到的：

| 节点 | 阶段 | 横幅 | 操作记录 |
| --- | --- | --- | --- |
| 取货移动中 | `AwaitingPickupArrival` | 需要恢复 / `DEPARTURE_SAFETY_NOT_READY` | 仅启动两行 |
| 到站待录入 | `AwaitingSublot` | 可扫码，已到站 | `收到子批录入请求：Q26085155-5。` |
| 开往关卡 | `AwaitingGateArrival` | 需要恢复 / `DEPARTURE_SAFETY_NOT_READY` | 1号仓操作完成 → 结果已被服务端确认 → **收到重复仓位命令，已保持原结果重放，未再次执行仓门 IO** |
| 完成 | `Completed` | 就绪 | 卸货同样投影 |

顶栏也在跟随：「到站」从未到站 → `N2-4_N3-4 / Q26085155-5` → `关卡 / Q26085155-5`，「发车安全」随安全闸门在禁止／允许之间切换——正是票 14 记录过的闸门行为，区别是现在**操作员看得见**，而不是被钉死的「连接中」横幅挡住。

**新发现（外观级，归车载端只读仓，不阻断）**：操作记录面板混用两个时区——「系统」启动行是本地时间（`16:43:12`），而 `31263b1` 新增的「操作」／「成功」行是 UTC（`08:45:21` 等），业务条目看起来比它上面的启动条目早八小时。四张截图里都可见。

---

## 5. 从 0.1.0 沿用的结论，及其边界

| 结论 | 能否沿用 | 依据 |
| --- | --- | --- |
| 单元与集成测试 243 passed / 0 failed / 0 skipped | **能** | 服务端产品代码是同一 commit，车载端未参与该套测试 |
| 协议一致性 G2，W2G-IS-00～07 **8/8 PASS** | **能** | 绑定 manifest `a467c0c4…`，本候选协议身份与之逐字相同 |
| 现场端到端闭环（取货→装货→`TO_GATE`→卸货→`Stage=Completed`） | **不必沿用** | 已在本候选上用真车真单重跑通过，见 4.3。0.1.0 那轮的 generation 7 记录仍然有效，但不再是本候选的依据 |
| 干净安装验收 23 PASS / 0 FAIL / 5 INCONCLUSIVE | **部分沿用** | 服务端安装／卸载侧已在本轮重新验证（隔离安装 PASS），车载端行为侧由 4.3 覆盖；仍未覆盖的是真实 IO 与目标终端硬件两条，那本来就是 INCONCLUSIVE |

---

## 6. 已知限制（发布时原样携带，不得省略）

1. **`RecoveryRequired` 无操作员出口 —— 跨端结果身份缺口，未决。**（0.1.0 已知限制第 1b 项，原样保留）
   站点操作落进 `RecoveryRequired` 后，现场操作员在随包 HMI 上没有前进或撤销的手段。经两端核对，车载端对恢复命令保持 fail-closed 是当前唯一正确的行为：`SlotOperationResumeCommand` 复用原 `slotOperationAttemptId` 且没有独立的 resume 结果消息，而 ControlServer 以 `(slotOperationAttemptId, forcedRecoveryGeneration)` 唯一接受 `OperationResult`，`RESUME_AFTER_REPAIR` 又不推进 `forcedRecoveryGeneration`。修好物理状态后发同 attempt 的新结果会被判内容冲突，重放原结果会被按 replay 忽略，两条路都不收敛。
   收敛需要两端先选定一个结果身份方案（resume 命令携带新的 operation/result identity；服务端在活动 `recoveryActionId` 下允许同 attempt 的授权替代结果；或协议新增独立的 resume result 并明确它如何关闭原 `OperationResult`），涉及协议仓变更与两名负责人批准，**判为本候选范围外**。在方案选定并通过带 demand 的联合回归之前，`RecoveryRequired` 的现场处置须走人工流程。
   0.1.0 已知限制第 1a 项（状态与操作记录不反映业务）**在本候选内已收进修复并经现场闭环验证**，见 4.2 与 4.3。

2. **真实八仓 IO 与车载目标终端硬件未取得资格。**
   4.3 的现场闭环里 IO 全程是模拟器 `127.0.0.1:1502`，车载端跑在开发工作站上。这**不构成**真实 IO 模块、接线、锁或光幕的资格，也不构成车载目标终端（屏幕、触摸、扫码枪）的资格。上线这两项前，本候选只适用于受控测试环境。

3. **服务端正确性依赖一条协议未强制要求的对端行为。**
   服务端每个迭代重发未确认的会话快照；当前车载端对重复快照会再次 ACK，因此快照重发冲突不可达。一个沉默但仍然合规的对端（收到重复快照不再 ACK）会让该冲突可达且自维持。更换或修改对端行为的一方需要重新评估这一点。**本候选换过车载端二进制，该结论未在 `31263b1` 上重新核对。**

4. **车载端随包配置是开发默认，上线前必须逐项改正。**
   `appsettings.json` 中 `environment=Development`、`wireToGate.enabled=false`、`vehicleSafety.endpoint` 是 `.invalid` 占位符，另有 7 个现场占位符：`REPLACE_CONTROL_SERVER_HOST`、`REPLACE_CONTROL_SERVER_IP`、`REPLACE_IO_MODULE_IP`、`REPLACE_RULE_SERVER_IP`、`REPLACE_WITH_64_CHARACTER_SHA256`、`REPLACE_WITH_EXPECTED_VEHICLE_KEY`、`REPLACE_WITH_STABLE_UUID`。
   默认 `appsettings.json` 的 `onboardBuildCommit` 仍声明为 `a6f05fb…`，而二进制建自 `31263b1…`（`declaredBuildCommitMatchesBuild=false`）；随包的 `appsettings.Production.template.json` 已由构建脚本填成正确的 `31263b1…`，**上线请以该模板为准整体替换 `appsettings.json`**。
   注意：`environment=Production` 会拒绝回环地址，配错的表现是弹「软件无法启动」且**不产生任何日志目录**。

5. **服务端随包 `appsettings.json` 携带现场真值。**
   `RIoT.baseUrl=http://172.19.206.222:8888`、`vehicleKey=BROKERX-0c20ff06…`、`mapId=25`、中文站点名，随发布物一起分发（本仓库为 PRIVATE，发布已知情授权）。
   三个 runtime 开关 `RiotCreateDispatch`、`JourneyRuntime`、`OnboardSafetyProjection` 出厂均 `enabled=false`，因此**装完不建单、不动车**；要跑业务须显式打开。

6. **手册第 11 节关于 `dispatchGeneration` 的说明不完整（更正见下节）。**

---

## 7. 手册更正（包外，不修改资产）

包内 `RELEASE-CANDIDATE.md` 第 11 节只泛说「正数 `dispatchGeneration` 必须核验」，未说清何时必须递增。精确规则是：

`JourneyRuntimeEngine.cs` 构造的上层订单键为

```
upperId = W2G-{demandId}-{PICKUP|GATE}-{dispatchGeneration}
```

`demandId` **在键里**，所以：

- **新的 demand 用 `dispatchGeneration=1` 不会撞已有订单**；
- **只有重跑同一个已派过的 demand** 才必须换更大的代次。现场 1～7 已经用掉，重跑同一 demand 须从 8 起。

本更正不写进包内——`RELEASE-CANDIDATE.md` 在 868 条哈希之内，改动它会使全部哈希与 `release-manifest.json` 失效并要求整体重建候选。

---

## 8. 范围与支持责任

- 本 release 只在 `8005-agv-control-server` 创建。**车载端 `8005-agv-onboard-hmi` 对应 `31263b1` 的 tag 不在本次发布范围内**，需由该仓负责人自行创建；该仓已有的 `w2g-mvp-rc-0.1.0` tag 指向 `304e6ad`，是 0.1.0 的引用，不要复用。协议仓 `protocol-v0.1.1` 未改动。
- 交付判定是「程序能安装、运行并完成规定业务闭环」。本候选已就安装、启动、会话建立、HMI 可见性与**含新车载端的真车端到端闭环**（4.3，`Stage=Completed`）逐条给出证据。
- **这不等于工厂生产可用**：已知限制第 1、2 项分别阻断 `RecoveryRequired` 的操作员出口与两项硬件资格。
- 真实车辆动作、Golden WPF tier 2/3、工厂试运行仍需逐次单独授权并满足现场物理安全条件，不因本 release 存在而获得授权。
- 外部秘密（`CONTROL_SERVER_ONBOARD_CREDENTIAL`、`CONTROL_SERVER_RIOT_CALL_API_KEY`、`CONTROL_SERVER_MES_INGEST_SHARED_SECRET`、`CONTROL_SERVER_OPERATOR_ID`）只以环境变量名出现在包内脚本与手册中，值不在包内、不进 Git、不进日志。

