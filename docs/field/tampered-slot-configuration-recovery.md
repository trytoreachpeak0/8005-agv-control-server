# 仓位配置被改动过的车怎么恢复

**口径（产品负责人 2026-09-12 定）：把车上的生效配置改回已批准的那一版。**服务端不为迁就车上的现状发布一个新版本——
那等于承认那次改动合法，而车上的配置从来不是权威（REQ-0258）。

**这条路径还没有被任何门禁证过。**`FP-IS-14` 的 G3 证到的是「车被改过之后，服务端拒绝激活、不动生效版本、车仍在线」
（`evidence/g3/20260910-fp-is-14-fingerprint-mismatch-mapped`）；「改回来之后车重新就绪」没有跑过。见文末。

## 一、怎么认出这台车

三处读数同时成立：

| 读哪里 | 读到什么 |
| --- | --- |
| 服务端 `SessionRecoveries` 里这台车的行 | `Readiness` 为 `RecoveryRequired`，`ReasonCode` 为 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH` |
| 同一行的 `ReportedSlotConfigurationFingerprint` | 与 `ActiveSlotConfigurations` 里这台车的 `Fingerprint` 不同 |
| 不可改写业务审计 | 有 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH_OBSERVED`，`detailJson` 里写着期望与车报的两个指纹 |

车是在线的——会话照建，只是拿不到业务就绪、不会被派活。这是 2026-09-10 定的形状：指纹不符不再把车挡在会话之外，
否则连下面这些远程步骤都做不了。

## 二、动手之前确认两件事

**1. 服务端没有这台车待补报的激活。**

```sql
SELECT ActivationId, State, IssuedAt FROM SlotConfigurationActivations
WHERE AgvId = '<agvId>' AND State = 'PENDING_RESULT';
```

必须是零行。下一步会移走车上那份生效配置文档，而文档里同时存着激活结果的历史——服务端重连后按
`SLOT_CONFIGURATION` 重发一条还没结的命令时，车正是靠那份历史认出「这一次已经有结论」、原样补报而不重新激活。
有待补报的激活就先让它收敛（车在线时它会自己收敛），不要在它中间移文件。

**2. 车上 `appsettings.json` 的 `ioModule.slots` 仍是已批准的那一份。**

文档不存在时，车就从这里生成初值。已批准的八仓硬件事实（REQ-0267）是：`doChannel` 0～7、`lockFeedbackDiChannel`
0～7、`lightCurtainDiChannel` 8～15（渲染成 `DO1～DO8`／`DI1～DI8`／`DI9～DI16`），`signalPolarity` 为
`ACTIVE_HIGH`，`pulseResetMilliseconds` 为 500。如果改动的人连 `appsettings.json` 也改了，先把它改回来，
否则下面生成出来的仍是被改过的那一版。

## 三、步骤

每一步都是**改一台生产车上的文件**，每台车、每一次单独在对话里取得授权，不在批量里顺手做。远程命令从 PowerShell
工具发，不从 Bash 工具发（Bash 里的 `scp` 是 MSYS 版，认不了 `C:\` 路径）。

1. **停车载端进程**（`SQCD.Agv.Wpf`）。车停着、不在执行作业时再停。
2. **把生效配置文档移走并留档**：安装根下的 `active-slot-configuration.json`（路径以 `appsettings.json` 的
   `wireToGate.activeSlotConfigurationPath` 为准，缺省相对于安装根 `C:\8005\OnboardHmi`），改名为
   `active-slot-configuration.tampered-<yyyyMMddTHHmmss>.json`，**不删**——它是这次改动的证据。
3. **启动车载端**：`remote-ops/onboard-hmi/scripts/10-start-onboard-stack.ps1`。车从 `appsettings.json` 生成初值，
   在完整握手的 `CapabilitySnapshot` 里报出它的指纹。
4. **读服务端确认**，下一节。

**为什么移走文件，而不是手改里面的 `Slots`：**`ActiveSlotConfiguration.Fingerprint` 是从 `Slots` 现算的，手改
JSON 很容易改出一个与已批准事实差一位的版本；而「文档不存在时从 `appsettings.json` 生成」正是这台车第一次部署时
走的那段代码，不需要任何人手抄通道号。

## 四、怎么算恢复成功

| 读哪里 | 应当读到 |
| --- | --- |
| `SessionRecoveries` 这台车的行 | 会话代比恢复前大 1；`ReportedSlotConfigurationFingerprint` 等于 `ActiveSlotConfigurations` 的 `Fingerprint` |
| 同一行的 `ReasonCode` | **不再是** `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH` |
| 看板车队会话卡片 | 这台车一行显示就绪与原因码，不是「车辆失联」 |

原因码不再是指纹不符，不等于车一定 `Ready`——其它就绪条件（出发安全、恢复会话、仓位配置就绪门禁）照旧各管各的。

生效配置版本号会和服务端对不上（车上是 `appsettings.json` 里的 `activeSlotConfigurationVersion`，服务端是上一次
激活记下的版本）。**就绪判定只比指纹，不比版本号**，所以这不影响恢复；下一次正常激活之后两边的版本号自然重新一致。

如果恢复之后仍是 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`：车生成出来的仍不是已批准那一版，回到第二节第 2 条。

## 五、这条路径还欠一份证据

可以在 `scripts/run-staged-g3-restart.ps1` 里加一个阶段：拒绝路径断言跑完之后，照本文第三节移走生效配置文档、
再重启车载端，断言会话代加一、原因码不再是 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`、生效配置一个字段没动。
那是一次 `FP-IS-14` 的 G3，进门禁要产品负责人批准，本文不自行加。
