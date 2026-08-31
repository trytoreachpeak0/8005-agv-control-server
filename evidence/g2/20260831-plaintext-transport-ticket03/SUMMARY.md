# 明文传输改造（票 03）安装链拆证书取证

日期：2026-08-31。基线：`ControlServer_MVP@ae4a17d`（票 02）。改动范围：`scripts/` 下的安装、卸载、
更新脚本与三个 G3 runner。发布手册与 `README.md` 不在本票，留给票 04。

## 完成度声明（先读这一节）

**票 03 的完成判据尚未满足。** 判据要求在隔离实例上跑通「安装→启动→停止→再启动→强制重启→卸载」
全流程，而 `Install-ControlServerLocal.ps1` 与 `Uninstall-ControlServerLocal.ps1` 都以
`Assert-Administrator` 开头，本轮会话不是管理员，无法执行。本目录里的
`Invoke-IsolatedLifecycle.ps1` 是为此准备好的隔离验收脚本（服务名 `8005 AGV ControlServer Ticket03
Probe`、独立安装／数据／备份根、端口 58405／58407），**尚未运行**，因此不存在
`lifecycle-report.json`。

下面记录的是**已经取到的**行为证据：安装链现在实际写出的配置能明文启动，以及升级路径的配置迁移
是载重的而非装饰。

## 探针形态

`Invoke-InstallChainProbe.ps1` → `probe-report.json`。隔离端口 58305／58307、隔离 SQLite、隔离
content root。已安装的生产服务全程 `Running`，监听仍为 `127.0.0.1:58005` / `127.0.0.1:58007`，
未受影响。

两份被测配置**都不是本文复述的**，而是用 PowerShell AST 从脚本里抽出 `$configuration` 赋值的字面量
后求值得到的——现行值取自工作树的 `Install-ControlServerLocal.ps1`，TLS 期基线值取自
`git show e5ee065:scripts/Install-ControlServerLocal.ps1`。迁移函数同样是从
`Update-ControlServerLocal.ps1` 里抽出 `Convert-RetainedConfigurationToPlaintext` 定义后执行的，
不存在探针与脚本各写一份而悄悄分叉的可能。

## 判据回读

| 判据 | 回读结果 |
| --- | --- |
| 安装脚本现在写出的配置能明文启动 | `Onboard NDJSON listener started on 127.0.0.1:58305; transport=plaintext` |
| 两个端口都真的在监听 | `127.0.0.1:58305`、`127.0.0.1:58307`（`Get-NetTCPConnection -OwningProcess`） |
| `/health/live` 经 HTTP | `{"status":"live"}` |
| 启动日志零证书字样 | 唯一命中是建表 SQL 的列名 `"HttpStatusCode" INTEGER NULL`，非证书 |
| 迁移后的配置里无残留证书键 | `residualCertificateKeys: []` |

## 红侧：不迁移就起不来（`red-side-unmigrated-legacy-config.txt`）

`Update-ControlServerLocal.ps1` 会把已安装的 `appsettings.Production.json` 原样带进新安装目录。
票 02 之后这等于把新二进制显式拒绝的键喂给它。把 `e5ee065` 版安装脚本生成的配置原样交给当前
二进制：

```
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException:
OnboardTransport:serverCertificatePath was removed in this version; ... ;
OnboardTransport:serverCertificatePasswordEnvironmentVariable was removed in this version; ... ;
OnboardTransport:allowInsecureLoopback was removed in this version; ... ;
OnboardSafetyProjection:requireHttps was removed in this version; ...
```

进程退出，退出码 `-532462766`。**这就是升级路径不加配置迁移就必然失败并回滚的直接证据**，也是本票
超出 Question 字面范围加入迁移的理由。

## 绿侧：同一份文件迁移后能起来（`green-side-migrated-config.log`）

同一份 `legacy-appsettings.Production.json`，经 `Convert-RetainedConfigurationToPlaintext` 处理后：

- `removedKeys`：上述四个键，逐个报出；
- `healthUrlRewritten: true`，`https://localhost:58307` → `http://localhost:58307`；
- 进程启动，`/health/live` 返回 `live`，`transport=plaintext`。

红绿是**同一个检测器**（同一二进制、同一启动路径），差异只在那份配置文件迁没迁，所以这条红不是
恒红。

## 本票内做出的三处更正

1. **票 03 说三个 G3 runner 都带 `-InstallTemporaryCurrentUserRoot` 授权——不成立。** 全仓只有
   `run-staged-g3.ps1` 有该开关（`git grep` 在 `scripts/` 下唯一命中）。另两个 runner 的实际问题是
   相反方向的：`run-staged-g3-restart.ps1` 与 `run-demand-bearing-g3-vectors.ps1` 都注入
   `OnboardTransport__serverCertificatePath=''`，而票 02 的过时键校验用
   `GetSection().GetChildren()` 比对**键名**，空值也算键存在——**照原样它们在票 02 之后已经拉不起
   服务端了**。两处注入已删。
2. **`New-WireToGateReleaseCandidate.ps1` 不需要改动。** 票 03 把它列为承载点，但其中与证书相关的
   只有 secret scan 的 `-----BEGIN [A-Z ]*PRIVATE KEY-----` 规则、`pkcs12-password-literal` 规则与
   `.pfx/.p12/.pem/.key/.jks/.keystore` 扩展名清单。那是阻止密钥材料进发布包的门禁，不是证书机制；
   删掉等于削弱发布门禁，与本轮方向相反。**保留原样是有意决定，不是遗漏。**
3. **本轮自己引入又修掉一个回归**：数据根此前是被 `New-Item -Path $certificateDirectory -Force`
   顺带创建的。删掉证书目录后，全新安装会在 `Set-RestrictedDirectoryAcl $dataRoot` 处失败。已补上
   显式的 `New-Item -ItemType Directory -Path $dataRoot -Force`。该缺陷是在准备隔离验收脚本时静态
   核查出来的，**不是**由某次运行证伪的——生命周期验收仍然欠着。

## 无法在隔离实例上排练的部分

`Update-ControlServerLocal.ps1` 把服务名（`8005 AGV ControlServer`）与安装／数据／备份根硬写成生产
值，没有对应参数，因此升级流程**无法**在隔离实例上排练：唯一的执行对象就是生产服务。这是既有属性
而非本轮引入。本票因此只能以上面的红绿对照覆盖迁移逻辑与产品行为，未覆盖的是证书目录删除、机器级
口令清除与备份／回滚这三段的真实执行。是否为该脚本补参数化留给票 09 决定。

## 未清理的下游引用（属票 04）

`docs/RELEASE-CANDIDATE.md` 第 125／220／245／326／333 行与 `README.md` 第 92 行仍在描述
`-InstallCurrentUserRoot`、`-TrustedRootThumbprint`、`-InstallTemporaryCurrentUserRoot` 这三个已被
删除的参数。**第 245 行不在票 04 已定位的段落清单里**，实施票 04 时须补上。
