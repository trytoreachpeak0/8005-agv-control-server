# 2026-09-01 明文绑定上的 staged G3：三个 runner 的首次实跑

## 结论

三个 G3 runner 在**明文绑定**上各跑一次，全部通过，五十九条断言零失败：

| runner | 运行 ID | 结果 | 断言 |
| --- | --- | --- | --- |
| `run-staged-g3.ps1` | `20260831T180238976Z` | `STAGED_G3_RECOVERY_REPLAY_PASS` | 19 / 19 |
| `run-staged-g3-restart.ps1` | `20260831T180034188Z` | `STAGED_G3_PROCESS_RESTART_PASS` | 20 / 20 |
| `run-demand-bearing-g3-vectors.ps1` | `20260831T180416352Z` | `DEMAND_BEARING_G3_VECTORS_PASS` | 20 / 20 |

三个 `status` 与全部断言值都是 runner **自身发射**的机器可读结果，直接落在各自的
`run-result.json` 里，本文档只是转述，不是判定来源。三次 `failedAssertions` 均为空数组，
`secretLeakFiles` 均为空数组。

这是票 12 之后三个 runner 的**第一次真实运行**。票 02 只证服务端单侧，票 03 只证安装链，
票 12 只做了静态对齐与静态取证。票 12 修掉的 `run-staged-g3-restart.ps1:508` 那处失败，
至此不再只有静态证据。

**本次不是候选产物的验收。** staged G3 绑的是 commit，从 exact clone 重新 publish，不碰
票 08 冻结的候选包。候选包的干净安装验收是票 09，真车闭环是票 10。三次运行的
`classification` 也据实记为 `formalSlicePass: false`、`fullG3: INCONCLUSIVE`、
`releaseCandidate: INCONCLUSIVE`，四个正式切片（`W2G-IS-00`／`04`／`05`／`06`）全部
保持 `INCONCLUSIVE`。

## 先决动作：绑定移到候选身份

`scripts/run-staged-g3.ps1:11` 的 `$ControlServerCommit` 此前锁 `3d8b00c7558ae700358f1f995a5ac75d12a3250c`
——TLS 期服务端。配上绑定里已有的明文车载端，真跑必然握不上手。票 12 具名留下了这个前置，
目标值等票 08 定候选身份。

本次改为 `56d4b1cc2f26325ca853acc4e7278bbde8651874`，即票 08 冻结进候选 0.2.0 的服务端提交，
**不是**其后的证据提交 `eaaa5b1`。`$OnboardCommit` 未动：开跑前实测 `origin/OnboardHmi_MVP`
头仍为 `238b46eb2c9ae90584e4288a782176f66b7de942`，`New-ExactClone -RemoteRef` 的远端引用
校验因此继续通过。改动为单行，提交 `c0f1e84`。

## 冻结身份

- ControlServer：`56d4b1cc2f26325ca853acc4e7278bbde8651874`
- OnboardHmi：`238b46eb2c9ae90584e4288a782176f66b7de942`
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- runner／harness：`c0f1e84ed3b7b0355e6cdb5a4d84d4772ae75a42`，三次均 `runnerWorktreeCleanAtStart: true`
- 运行配置 SHA-256：staged `f4ba9086dd4b…`／restart `72fa5b5a232e…`／vectors `c931ad84671d…`

## 绑定回读

`run-staged-g3.ps1` 的 `param()` 块是三个 runner 的**唯一**绑定源，另外两个解析它而不各持副本。
改动后按三条各自的解析链分别回读，四个值一致，只有 `ControlServerCommit` 变：

- restart runner：`configuration.commitBinding.sourceSha256` =
  `62711a64a8eb859b817aac0c11fc95fcb5d73d7f17c5f6e665dadddb544edcb4`；
- vectors runner：同一 `sourceSha256`，另加
  `functionSourceSha256` = `90989268477eb1b167b8bda40b51a9b423a67787464ef98cc158091a85f46e85`
  （它连 `Get-SharedCommitBinding` 本身都从 restart runner 里提取）；
- 两者都等于当时 `scripts/run-staged-g3.ps1` 与 `scripts/run-staged-g3-restart.ps1` 的实测哈希。

更强的一条不是文件哈希，而是**运行中的进程自报**：restart runner 的
`sessionIdentity.serverBuildCommits` 在三次重启后各报一次
`56d4b1cc2f26325ca853acc4e7278bbde8651874`，`onboardBuildCommits` 三次各报
`238b46eb2c9ae90584e4288a782176f66b7de942`，对应断言
`runningControlServerReportsBoundBuildCommit` / `runningOnboardReportsBoundBuildCommit` 为 PASS。
参数写了什么与真正跑起来的是什么，在这里对上了。

## 红侧对照（`controls/red-side-controls.json`）

十三行，两个检测器各自双向，`RED_SIDE_CONTROLS_PASS`。

**A. `run-staged-g3-restart.ps1:515` 的 `useTls` 守卫**（票 12 修的那处）

| 方向 | 用例 | 期望 | 实测 |
| --- | --- | --- | --- |
| 前置 | 明文 `238b46e` 的车载端 `appsettings.json` 带 `useTls` | False | False |
| 前置 | TLS 期 `31263b1` 的同一文件带 `useTls` | True | True |
| RED | 在明文配置上无条件赋值 | `SetValueInvocationException` | `SetValueInvocationException` |
| GREEN | 守卫式赋值不抛 | 不抛 | 不抛 |
| GREEN | 写回后的明文配置仍无 `useTls` 键 | False | False |
| CONTROL | 守卫式赋值在 TLS 期配置上**进得去分支**并写 False | False／不抛 | False／不抛 |

CONTROL 那一行是关键：只有 RED 与 GREEN 的话，一个恒不进分支的坏守卫看起来同样合理。

**B. `Get-SharedCommitBinding`**

四个值对着真文件读回全绿；三个变异体各自证红——老 SHA 的副本读回老 SHA（证明读的是文件而
不是内建常量），大写 SHA 与截断 SHA 各自按 `-cnotmatch '^[0-9a-f]{40}$'` 抛出。

## 「无 TLS」不是靠字面量（`controls/corroboration.json`）

三个 runner 的 `tls = $false` / `temporaryTrustRootInstalled = $false` /
`certificatesGenerated = $false` 都是硬编码字面量，本身不构成证据。二十四行独立观测佐证，
`CORROBORATION_PASS`：

- 四个证书存储跑前跑后的**指纹集合摘要**逐一相同：`CurrentUser\Root` 44 张
  （`eabac27de65eeed4…`）、`CurrentUser\My` 1 张、`LocalMachine\Root` 42 张、
  `LocalMachine\My` 1 张。不是只比数量。
- 三个 stage 根与三个证据根递归扫 `*.pfx *.p12 *.cer *.crt *.key *.pem *.der` 与 `certs\`
  目录，全部为 0。
- staged 与 restart 实际 publish 出来的车载端 `appsettings.json` 回读：`wireToGate` 下
  `useTls`／`serverCertificateSha256`／`serverCertificatePath` 一个都不存在。
- 全部 runner 日志搜 `Schannel|SslStream|AuthenticationException|X509|https://127.0.0.1|certificate`
  命中 0。票 07 记过，错配时唯一点出真因的就是服务端的 `AuthenticationException`；这里一条没有。
- vectors runner 的 `publish\` 只有 `control-server` 一个目录——它用的是从主 runner 提取的合成
  对端，从不克隆或发布车载端。这条按设计事实断言，若哪天真冒出一个车载端 peer 会翻红。
- 生产服务 `8005 AGV ControlServer` 全程 PID 8632、`Running`，进程启动时间早于本轮，未被重启。
- 只读仓 `8005-agv-onboard-hmi` 工作树零改动，HEAD 仍 `bbfbc52f…`。红侧用的两份车载端配置
  是在 stage 的一次性 exact clone 里用 `git show` 取的。

## 端口与隔离

- staged 58205／58207，restart 58105／58107，vectors 58305／58307；共用 Modbus 1502 与模拟器
  HTTP 58006，三次运行**串行**，无重叠。
- 生产服务占 58005／58007，与三者全部错开。开跑前逐个核过监听端口空闲。

## 未做

- 未安装、未卸载、未升级任何 Windows 服务；
- 未创建 RIoT 订单、未发送移动命令、未使用现场凭据、未伪造停稳／驻车信号；
  三次的 `noMovementOrExternalSideEffects` 均 PASS，`MesIngest`／`RIoT` 均指向死端口
  `http://127.0.0.1:1`；
- vectors runner 恢复的是一份**真实授权运行**留下的库
  （`storeProvenance.fieldRunRoot` = `C:\Users\szy\w2g-stage\run\fullloop-20260829T131549Z`，
  `fieldDatabaseSha256` = `87220f7990990106…`），不是伪造或隔离实例的库。

## 目录

- `staged/`、`process-restart/`、`demand-bearing-vectors/` —— 三次运行的原样证据，各含
  `run-result.json`、`configuration.json` 与 `logs/`。
- `controls/` —— 红侧与佐证的结果 JSON，以及产出它们的三个脚本
  （`Invoke-Ticket13RedSide.ps1`、`Invoke-Ticket13Corroboration.ps1`、`Get-CertSnapshot.ps1`）
  与跑前的证书存储快照 `cert-before.json`。
