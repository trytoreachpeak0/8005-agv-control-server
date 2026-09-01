WIRE_TO_GATE MVP Release Candidate 0.2.0。**受控工厂内网专用，未取得工厂生产资格**——见第 6 节已知限制，其中第 1～3 条是本版本新引入的代价。

本候选相对 `w2g-mvp-rc-0.1.1` 只做一件事：**把 OnboardHmi ↔ ControlServer 的两条链路由 TLS/HTTPS 改为明文 TCP/HTTP，并从产品代码、安装链与发布手册中删除整套证书机制**。动机是部署复杂度——工厂内网、两端都是自有应用，为此维护证书信任链累赘且抬高部署成本。业务功能与协议身份未变。

**版本号取 `0.2.0` 而不是 `0.1.2`。** 新旧两端不可互通，旧站点配置会被**显式拒绝启动**，patch 位会被读成可就地替换。`0.1.0` / `0.1.1` 保留且不可变。

包内 `RELEASE-CANDIDATE.md` 仍是唯一操作入口。本说明只做包内文件做不到的事：版本绑定与资产哈希、本轮实际验证到哪一步、哪些结论是沿用的、以及原样携带的已知限制。

---

## 1. 版本绑定

| 组件 | 仓库 / 分支 | commit | 相对 0.1.1 |
| --- | --- | --- | --- |
| ControlServer | `8005-agv-control-server` / `ControlServer_MVP` | `19ce7db70893afea6c6361988c3bc612d77569d0` | **变**（0.1.1 是 `9daeef4325fccf094767b689fa484d8e1e414042`） |
| OnboardHmi | `8005-agv-onboard-hmi` / `OnboardHmi_MVP` | `238b46eb2c9ae90584e4288a782176f66b7de942` | **变**（0.1.1 是 `31263b1ffd372db1f27af5e1143ebad7e7679715`） |
| 协议 | `8005-agv-protocol` / tag `protocol-v0.1.1` | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | 未变 |

**协议仓一个字都没改，这是本轮的一条硬约束。** 协议对 `credentialProof` 只规定「是 `minLength: 1` 的字符串且必填」，不规定取值与保护方式；tracked 文件搜 `tls|ssl|certificate|encrypt|证书|加密` 命中 0，`transport` 仅出现于 `transportDedupKey` / `transportDemandKey`，`58005` 零出现。传输层安全形态本来就不在协议约定内，因此本轮不触发协议变更的双人批准门禁。

协议身份（`ProtocolReleaseIdentity`，`profileId=WIRE_TO_GATE_MVP`、`protocolVersion=1`、`approvalStatus=APPROVED_RELEASE`），从产物读回而非复述，**九个字段与 0.1.1 逐字相同**：

| 字段 | 值 |
| --- | --- |
| `repositoryCommit` | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` |
| `manifestSha256` | `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f` |
| `schemaBundleSha256` | `e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c` |
| `vectorsSha256` | `fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e` |

**程序集哈希对内容什么都不能证明**（托管构建每次产生新 MVID，票 25 已证）。本轮的内容判据是符号级，见第 4.1 节。

---

## 2. 资产与校验

| 资产 | 大小 | SHA-256 |
| --- | --- | --- |
| `w2g-rc-20260901b-19ce7db.zip` | 123,991,314 B | `abbff6e0d9afe415080b774395695bff4dad9f6437a9455f3e3c6d84aa1ceca9` |
| `release-manifest.json` | 171,966 B | `2389853936f9453c2fa93168c6d1316f0e83ba0367246801be98653aa54df3d9` |
| `SHA256SUMS.txt` | 99,003 B | `5e3aeb3ab94a29723b9c7e2c79b5bbc2548777a78f181295368363e7e7cdbd02` |

后两个文件与 zip 内根目录下的同名文件是同一份，单独上传只为不下载 118 MB 也能读到清单。zip 解压出单个顶层目录 `w2g-rc-20260901b-19ce7db/`，**869 个文件**（0.1.1 是 868）。

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

无输出即 869 个文件全部一致。发布前在本机复验为 `OK=869 MISMATCH=0 MISSING=0`，且 `unlistedFilesInReleaseRoot=0`——发布根里没有未被哈希覆盖的载荷。该校验器**证明会红**：把 `controlserver\appsettings.json` 复制到发布根之外再追加一个字节，同一条比对由 MATCH 变 MISMATCH（`e6557aad…` → `3c6d96b8…`）；产物本身未被修改，事后重新哈希仍等于清单值。

---

## 3. 与 0.1.1 逐文件的差异

| 组件 | 相同 | 变更 | 新增 | 删除 |
| --- | --- | --- | --- | --- |
| `controlserver/` | 372 | 11 | 0 | 0 |
| `onboard-hmi/` | 464 | 13 | 0 | 0 |
| 包根与 `scripts/` | — | — | **1** | 0 |

- 两个组件目录的变更项全部是托管程序集、其 pdb、两个 apphost 与随之变化的 manifest——**重建噪声，不承担任何内容主张**，内容主张在第 4.1 节；
- 唯一的新增文件是 `scripts/Update-ControlServerLocal.ps1`（清单 868 → 869 条）。它补的是本轮引入的一个**交付缺口**：手册 §4.5 让站点用包内相对路径运行该升级脚本，而 0.1.1 的复制清单里没有它，交付包跑不了自己文档化的升级路径。
- 两个 apphost 是唯一一类哈希差异有意义的文件——它们不由源码编译，Win32 版本资源里嵌着 `InformationalVersion`。两个服务端 apphost 各 152064 字节，差异恰好 38 字节且全部落在连续窗口 `0x24D6C–0x24DBB` 内，解码即 `ProductVersion 1.0.0+56d4b1cc…` → `1.0.0+19ce7db7…`，窗口之外逐字节相同。

---

## 4. 本轮实际验证了什么

以下全部针对**本候选**，不是沿用：

| 门禁 | 结果 |
| --- | --- |
| 两端构建警告 | ControlServer 0，OnboardHmi 0（脚本对 >0 直接 throw） |
| 发布扫描闸门 | 秘密 finding 0、密钥材料文件 0、UNRESOLVED 许可证仅三个具名自建 RIoT SDK 包 |
| 869 条哈希全量回验 | `OK=869 MISMATCH=0 MISSING=0`，并证明会红 |
| 证书机制确已从二进制消失（符号级） | 见 4.1 |
| 跨机明文联调 | 见 4.2 |
| staged G3 三个 runner | 见 4.3 |
| 干净安装验收 ＋ 升级路径排练 | **35 PASS / 0 FAIL / 4 INCONCLUSIVE**、**18 / 18**，见 4.4 |
| **现场端到端闭环（真车、真单、generation 8）** | **`Stage=Completed`**，见 4.5 |

### 4.1 证书机制确实从出厂字节里消失了

扫每个组件目录下全部 `.dll` / `.exe`（383 与 477 个文件），原始字节按 Latin-1 解码后做**大小写敏感**的 ordinal 查找。

| 方向 | 符号 | 0.1.1 产物 | 本候选 |
| --- | --- | --- | --- |
| 消失（服务端） | `OnboardTlsCertificateLoader`、`CreateTransportStreamAsync`、`AllowInsecureLoopback`、`ServerCertificatePasswordEnvironmentVariable`、`RequireHttps` | PRESENT | **ABSENT** |
| 消失（车载端） | `ValidateServerCertificate`、`CreateTrustedHttpClient`、`ServerCertificateSha256`、`UseTls`、`CreateTransportStreamAsync` | PRESENT | **ABSENT** |
| 新增 | `OnboardTransportOptionsValidator`、`RejectRemovedTransportKeys` | ABSENT | **PRESENT** |
| 对照 | `OnboardTcpServer`、`HandleClientAsync`、`WireToGateSessionClient`、`ControlServerVehicleSafetySignalProvider` | PRESENT | PRESENT |

**检测器在两个方向上都被证明会响**：同一读取器在两个包里都能找到四个对照符号，且在新包里能找到本轮新增的两个符号——因此那十个 ABSENT 是真阴性而不是坏读取。

一个必须具名的框架携带者：`RequireHttps` 在 `Microsoft.AspNetCore.Mvc.Core.dll` 里也在（ASP.NET Core 自己的 `RequireHttpsAttribute`），两个包都带且未变。判定按两端**自有程序集**做，在 `ControlServer.Host.dll` 里它是 PRESENT → ABSENT。

运维实际要改的两个配置文件里证书字段也确实消失了：`controlserver/appsettings.json` 少了 `serverCertificatePath`、`serverCertificatePasswordEnvironmentVariable`、`allowInsecureLoopback`、`OnboardSafetyProjection.requireHttps`；`onboard-hmi/appsettings.Production.template.json` 少了 `useTls` 与 `serverCertificateSha256`，投影端点由 `https://` 变 `http://`。**现场占位符由七项减为六项**，少掉的正是 `REPLACE_WITH_64_CHARACTER_SHA256`。

### 4.2 跨机明文联调（真正的两台机器）

宿主 `192.168.200.1`（服务端）↔ Hyper-V guest `192.168.200.50`（车载端）建立 **4 次会话**，`SessionHello` / `CapabilitySnapshot` / `SafetyStateSnapshot` / `RecoveryStateReport` 各 4 条、`Heartbeat` 108 条全部以明文 NDJSON 过网；安全投影经明文 HTTP 送达且 `vehicleKey` 精确匹配。断连重连代次 1→2→3→4 单调推进——**协议层行为未因换传输层而改变**。

环境用 `environment=Production` 而非 Development，因为那是车载端 `IsForbiddenProductionHost`（拒绝 loopback）唯一生效的模式，从原理上排除了 loopback 蒙混。判对端可达全程只用返回 body 的往返（本机全局代理会让 ping 与 TCP connect 对不存在的主机也成功）。

### 4.3 staged G3：三个 runner 全绿

staged **19/19**、restart **20/20**、vectors **20/20**，五十九条断言零失败，全部由 runner 自身发射。比文件哈希更强的判据是**进程自报**：restart 的 `sessionIdentity` 跨三次重启各报一次服务端与车载端 commit，证明握手的确实是候选服务端与本候选车载端。红侧十三行，两个检测器各自双向。

**据实记录：这三次 G3 跑在 `56d4b1c` 上，不是 `19ce7db`。** 两个 commit 之间 `git diff --name-only 56d4b1c..19ce7db -- src tests Directory.Build.props Directory.Packages.props global.json` 列出 **0 个文件**——重建换的是身份标签不是产品，因此未重跑。不假装重跑过。第 4.4、4.5 两节的验收则都在 `19ce7db` 上。

### 4.4 干净安装验收与升级路径排练

隔离实例、用候选**自带**的脚本装与卸，绑非 loopback 地址：

- **35 PASS / 0 FAIL / 4 INCONCLUSIVE**（四条 INCONCLUSIVE 见第 6 节已知限制第 6 条）；
- **「安装不碰证书」在真实安装里证到**：四个证书存储（`CurrentUser\Root` 44、`LocalMachine\Root` 42、两个 `My` 各 1）的**排序指纹集合摘要**在安装期与「安装到卸载」全程各零变动——是摘要不是计数，换掉一张证书计数不变而摘要会变。无 `certs\` 目录，安装／数据／备份三个根零密钥材料文件；
- **独立佐证**：全程 30461 行服务端日志搜 `Schannel|SslStream|AuthenticationException|X509|certificate|https://` 命中 **0**；
- `SAFETY-PROJECTION-HTTP-AUTH` 明文 HTTP 200、无凭据 **401**；`HEALTH-LIVE-HTTP` 直连 http 返回 `{"status":"live"}` 200；
- **§4.5 从证书版本升级的排练 18 / 18**：在老 TLS 期候选装出的隔离实例上跑，证书目录删除、机器级证书口令清除、**备份回滚**三段第一次有了真实执行证据。回滚是用「manifest 自洽但起不来」的包把失败逼到备份之后触发的——回滚后安装树 0 差异、口令原值恢复、`certs\` 带 3 个文件回来。
- 红侧 **7 / 7**：检测器抽成模块，红侧脚本 `Import-Module` 的是同一份模块而不是复刻，七个检测器各在未改输入上绿、在单字段变异上红。

### 4.5 现场端到端闭环，在本候选上跑通（generation 8）

用户给出现场物理安全 GO。demand `Q26084908-12|WIRE_TO_GATE`，取货站 `N2-13_N3-13`/29，关卡 210，两筐落 1、2 号仓，全程 **11 分 25 秒**。

**终态 `Stage=Completed`、`BlockReasonCode` 为空、`SessionGeneration` 全程 1**（车载端「上层会话已建立」12 分钟内只出现一次，无重连、无代次跳变）。两条真单 `W2G-…-PICKUP-8` / `W2G-…-GATE-8` 各建一次、各五步审计齐全，`RiotDispatchAuditEvents=10` 无重复建单，`Load` / `Unload` 均 `Committed`。

**明文形态下的安全闸门（本轮的必须重证项）**：两条腿各出现一次「移动中拦、停稳自动放行」，都没有卡死旅程——取货腿挂在 `ONBOARD_SESSION_NOT_READY` / `DEPARTURE_SAFETY_NOT_READY` 后自动回 `Ready`，关卡腿同形态并落 `Completed`。安全投影本身走明文 HTTP，**556 次 GET 全部 200**。

**「本轮真的没有 TLS」用运行自己没写过的观测来证**：进程自报 `transport=plaintext` 与 `Now listening on: http://192.168.200.1:58707`；车载端 `environment=Production` 绑非 loopback，是被 `IsForbiddenProductionHost` 检过的事实；四个证书存储指纹集合摘要零变动；run root 递归零密钥材料文件；27336 行日志搜六个 TLS 词命中 0。红侧 **8/8**（四个检测器各双向，指纹那条特意让数量不变只换一张）。

### 4.6 测试

| 套件 | 结果 | 说明 |
| --- | --- | --- |
| 服务端单元与集成 | **249 passed / 0 failed / 0 skipped** | 跑在 `ae4a17d`；`git diff ae4a17d 19ce7db -- src/ tests/` 为空，覆盖的正是候选的产品源码 |
| 车载端 `SQCD.Agv.UnitTests` | **89 passed** | 在产出本批二进制的那份一次性克隆里跑 |
| 车载端 `SQCD.Agv.WireToGateG2Tests` | **24 passed** | 同上；两者 Failed 0 / Skipped 0 |
| 协议一致性 G2 `W2G-IS-00`～`07` | 沿用 0.1.1 的 **8/8 PASS** | 绑定 manifest `a467c0c4…`，本候选协议身份与之逐字相同 |

`W2G-IS-00` 的覆盖形态发生了变化，据实说明：`OnboardTlsCertificateLoaderTests.cs` 已删，删后 `git grep -niE 'schannel|sslstream|x509|tls' -- tests/` 零命中——**本仓再无任何测试触及传输层安全，因为已经没有 TLS 代码路径可供覆盖**。仅剩的两处证书相关是覆盖的反面：新增的否定测试断言残留的证书配置键会让启动失败。

---

## 5. 从 0.1.1 沿用的结论，及其边界

| 结论 | 能否沿用 | 依据 |
| --- | --- | --- |
| 协议一致性 G2 `W2G-IS-00`～`07` 8/8 PASS | **能** | 协议身份九个字段逐字相同 |
| 现场端到端闭环 | **不必沿用** | 已在本候选上用真车真单重跑通过（4.5）。0.1.1 的 generation 8 记录仍然有效，但不再是本候选的依据 |
| 干净安装验收 | **不沿用** | 全部在本候选上重跑（4.4），并新增升级路径排练 |
| 硬件资格两条（真实 IO、车载目标终端） | **原样沿用其 INCONCLUSIVE** | 本轮同样未取得，见已知限制第 6 条 |

---

## 6. 已知限制（发布时原样携带，不得省略）

前三条是**本版本新引入的代价**，是知情接受的选择而非缺陷：

1. **车载凭据以明文经网络传输。**
   `credentialProof` 是协议必填字段（`SessionHello.schema.json:73`），车载端把静态共享密钥原样放进 payload。改为明文之后，**在链路上抓一次包即可永久冒充车载端**，进而下发仓门 IO 命令。这是本轮改造知情接受的代价：挑战应答／HMAC 与来源 IP 白名单均已评估并否决——前者的成本是两端实现改造与一次协调升级（与本轮简化部署的动机相反），后者挡不住能抓包的人，判为安慰剂。

2. **安全闸门的输入经明文传输后可被篡改。**
   `motionState=STOPPED` 等安全信号在明文 HTTP 上传输，中间人可改写。4.5 证的是**受控内网下闸门按设计工作**（移动中拦、停稳放行，两条腿各一次），**不是**它在不可信网络下仍然成立。这是 safety 而非 security 层面的后果。

3. **Kestrel 单一绑定使 `/health/live` 与 `/version` 随投影端点一并暴露于厂内网。**
   本轮冻结的配置形态是单一绑定，异机部署时健康与版本端点跟着投影端点一起暴露。这是相对 0.1.1 新增的暴露面。安装脚本的 `-ListenAddress` / `-HealthBindAddress` 默认仍是 `127.0.0.1`；防火墙上 58005 / 58007 的**放行范围即暴露范围**。

4. **新旧两端不可混用，必须同版本升级。**
   0.1.1 与 0.2.0 的服务端／车载端不可互通，且旧站点配置会被**显式拒绝启动**（四个已删配置键任一存在即拒绝，`Key__Sub=''` 这类空值注入同样算键存在）。错配的失败形态**极易误判成网络不通**——两个方向的错误文本都不含 TLS 字样：明文车载端连 TLS 服务端时，车载端只说「ControlServer在会话恢复期间关闭了连接」，链路 B 只说「An error occurred while sending the request.」，唯一点出真因的是**服务端**日志里的 `AuthenticationException: Cannot determine the frame size or a corrupted frame was received.`；反方向老车载端连明文服务端只报 `Received an unexpected EOF or 0 bytes from the transport stream.` 并**无限重连不退出**。完整对照表与三步排除顺序见包内手册 §8.1。

5. **适用前提是受控的工厂内网。离开该前提不得使用本版本。**
   上面三条的风险敞口全部由「链路处在受控内网」这一前提兜底。该前提不成立时，本版本不适用——这不是配置问题，没有可打开的补偿开关。

6. **沿用 0.1.1 中仍然成立的其余已知限制：**
   - **真实八仓 IO 与车载目标终端硬件未取得资格。** 4.5 的现场闭环里 IO 全程是八仓模拟器（由用户选定），车载端跑在工作站上。这**不构成**真实 IO 模块、接线、锁或光幕的资格（`HW-REAL-IO`），也不构成车载目标终端（屏幕、触摸、扫码枪）的资格（`HW-ONBOARD-TARGET`）。
   - **`RecoveryRequired` 无操作员出口——跨端结果身份缺口，未决。** 站点操作落进 `RecoveryRequired` 后，现场操作员在随包 HMI 上没有前进或撤销的手段；`RESUME_AFTER_REPAIR` 的结果身份收敛需要两端先选定方案并涉及协议仓变更，判为本候选范围外。在方案选定前，`RecoveryRequired` 的现场处置须走人工流程。
   - **服务端正确性依赖一条协议未强制要求的对端行为。** 服务端每个迭代重发未确认的会话快照；当前车载端对重复快照会再次 ACK，因此快照重发冲突不可达。一个沉默但仍然合规的对端会让该冲突可达且自维持。
   - **车载端随包配置是开发默认，上线前必须整体替换。** `appsettings.json` 中 `environment=Development`、`wireToGate.enabled=false`，另有 6 个现场占位符。随包的 `appsettings.Production.template.json` 已由构建脚本填成正确的 `238b46e…`，**上线请以该模板为准整体替换 `appsettings.json`**。注意 `environment=Production` 会拒绝回环地址，配错的表现是弹「软件无法启动」且**不产生任何日志目录**。
   - **服务端随包 `appsettings.json` 携带现场真值**（RIoT baseUrl、vehicleKey、mapId、中文站点名），随发布物一起分发（本仓库为 PRIVATE，发布已知情授权）。三个 runtime 开关 `RiotCreateDispatch`、`JourneyRuntime`、`OnboardSafetyProjection` 出厂均 `enabled=false`，因此**装完不建单、不动车**；要跑业务须显式打开。

7. **旅程完成后的受理重放冲突（先于本轮存在，本候选携带）。**
   demand 仍留在 MES 目录时，旅程 `Completed` 之后的每次运行期轮询都抛 `BusinessIdentityConflictException: Accepted demand replay does not match its original order intent.`（`WireToGateStore.cs:235`），日志记 `Journey runtime iteration failed closed`。它 fail-closed、不污染已完成的旅程，但**会让运行期在完成后无法再受理其他合格需求**，直到该 demand 离开 MES 目录或服务重启。
   **不是本轮改造引入的**——调用链在 `WireToGateStore.AcceptCoreAsync` / `DemandIntakeService.AcceptCoreAsync`，与传输层无关，且 0.1.1 的 generation 7 完成之后出现同一条 `failed closed`。
   **修复已提交但不在本候选内。** `ControlServer_MVP@f0d6f1b`（「Stop offering an already-accepted demand back to journey intake」）让候选扫描在全部闸门之前把已受理的 demand 归为 `DEMAND_ALREADY_ACCEPTED`，并补了两条回归测试。它**晚于本候选的 `19ce7db`**，因此本候选的二进制不含它，本说明第 4 节的全部验收证据也不覆盖它——把它塞进已验收完的候选会让证据与交付物脱节。该修复将随后续版本发布，届时需要重跑干净安装验收与真车闭环。**本版本请按上面描述的行为运维。**

---

## 7. 相对 0.1.1 的运维变更摘要

不再需要做的事：生成自签根与叶证书、导出 PFX、把根证书导入 `CurrentUser\Root`、维护 `serverCertificateSha256` 指纹、设置机器级证书口令环境变量、续期。

新增或改变的事：

- 安装脚本新增 `-ListenAddress` / `-HealthBindAddress`（默认仍 `127.0.0.1`），异机部署时按手册 §4.4 设置；
- **从 0.1.x 升级**走手册 §4.5：升级器自动做三件事（迁移配置删掉四个已删键、删除证书目录、清除机器级证书口令），但 **`CurrentUser\Root` 里的旧自签根必须人工删除**——`certificateDirectoryRemoved: true` **不代表根证书没了**；手册给了两条指纹来源与回读确认步骤；
- `Update-ControlServerLocal.ps1` 已随包分发并参数化（五个参数，默认值等于原硬写值，生产调用形态逐字不变），可先在隔离实例上排练再动生产。

---

## 8. 范围与支持责任

- 本 release 只在 `8005-agv-control-server` 创建。**车载端 `8005-agv-onboard-hmi` 对应 `238b46e` 的 tag 不在本次发布范围内**，需由该仓负责人自行创建。协议仓 `protocol-v0.1.1` 未改动，其 tag / release 一并不在范围内。
- 车载端的四处传输层改动由该仓 owner 王昆执行并推送（`238b46e`），本轮对该仓写入为零；改动已由只读回读逐行核验通过。
- 交付判定是「程序能安装、运行并完成规定业务闭环」。本候选已就安装、升级、启动、跨机明文会话建立与**真车端到端闭环**（4.5，`Stage=Completed`）逐条给出证据。
- **这不等于工厂生产可用**：已知限制第 5 条限定了适用前提，第 6 条的两项硬件资格与 `RecoveryRequired` 出口仍然阻断。
- 真实车辆动作、工厂试运行仍需逐次单独授权并满足现场物理安全条件，不因本 release 存在而获得授权。
- 外部秘密（`CONTROL_SERVER_ONBOARD_CREDENTIAL`、`CONTROL_SERVER_RIOT_CALL_API_KEY`、`CONTROL_SERVER_MES_INGEST_SHARED_SECRET`、`CONTROL_SERVER_OPERATOR_ID`）只以环境变量名出现在包内脚本与手册中，值不在包内、不进 Git、不进日志。
