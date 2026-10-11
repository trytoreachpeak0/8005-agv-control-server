# control-server#548 证据：26 号图六类站点绑定的开工核实与本地演练

2026-10-11，工作站本机。**没有碰 factory01、MVP、任何 v2 实例的派车闸门；对 RIoT 只发了一次 GET。**

## 1. 开工核实：只读读 RIoT 26 号图站点表

命令（本仓 `scripts/field/Export-RiotStationCatalog.ps1`，只发一次
`GET http://172.19.206.222:8888/api/imap/v1/mapInfo/stations/26`，不重试，不写 RIoT；发之前确认到 RIoT 的路由走 `Wi-Fi`，不是 Clash）：

```powershell
pwsh -File scripts/field/Export-RiotStationCatalog.ps1 -MapId 26 -OutFile evidence/field/20261011-B10-05-map26-task-type-stations/catalog-26.json
```

输出见 `01-export-catalog-26.txt`：`Map 26: 216 stations read at 2026-10-11T08:41:06.8107577+08:00 via Wi-Fi`。
`catalog-26.json` 是 FieldOps `--catalog` 吃的形状，`catalog-26.raw.json` 是 RIoT 原样回复（带坐标）。

六个站逐个核对（站名按 Unicode 码点逐字比较，不是看终端显示）：

| 站号 | 站名 | 绑的任务类型 | 站名逐字一致 | 图上同名站数 | AREA 格式 | `pos.x` | `pos.y` | `pos.yaw` | `edge_id` | `type` |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 210 | 关卡 | `WIRE_TO_GATE` | 是 | 1 | 否 | -54950 | 38960 | 0 | 826 | 1 |
| 217 | 派工待送取货 | `STAGING_TO_WIRE` | 是 | 1 | 否 | -54850 | 37270 | 0 | 824 | 1 |
| 218 | 烘箱 | `DIE_TO_OVEN` | 是 | 1 | 否 | -16630 | -3530 | 0 | 128 | 1 |
| 219 | 三光 | `WIRE_TO_OPTICAL` | 是 | 1 | 否 | -55180 | 40400 | 6277.8 | 832 | 1 |
| 220 | 装片氮气柜 | `DIE_TO_WIRE_STAGING` | 是 | 1 | 否 | -55180 | 41860 | 0 | 838 | 1 |
| 221 | 焊线氮气柜 | `WIRE_TO_NITROGEN` | 是 | 1 | 否 | -55330 | 43010 | 0 | 844 | 1 |

- RIoT 的站点记录里**没有单独的「节点号」字段**；站的位置用坐标 `pos.*` 与它所挂的边 `edge_id` 表示，上表记的就是这两样。
- AREA 格式的判据是 `AreaNamedStationName`（按 `_` 拆段、每段符合 `^[A-Z][A-Z0-9]*-[0-9]+$`）；六个站名全是汉字，不可能命中。
- 与 10-03 那份（`evidence/field/2026-10-03-B9-charging-roster-and-policy/catalog-26.json`，211 个站）相比：**恰好多了 217～221 五个站，
  没有删站，也没有改名**。

## 2. 10-09 激活用的旧记录号

本票不碰 factory01，所以没有去实例库读。读的是 fp 上的现场证据
`evidence/field/20261010-W2-prep/13-activate-task-type-stations.txt`（10-10 换新库后重新激活的那次，dry-run 与正式激活两段）与
`02-before-v2-db-readonly.txt` 第 286 行（换库前旧库的只读读数），以及 v2 工作区看板 10-09 15:10 那条：
217 的 `siteVerificationRef` 是 **`MAP-26-STAGING_TO_WIRE-SITE-20261009`**，210 沿用预置的 `MAP-26-WIRE_TO_GATE-B6-IDENTITY-20260919`。
本票把两者换成 `...-SITE-20261010`。

## 3. 本地演练（dry-run、正式激活、只读核对、回滚，加三种错误候选）

**怎么搭的**：把 `scripts/l2` 整个复制到临时目录，只把副本里 `Invoke-L2Scenario.ps1` 的 `$mapId = 25` 改成 26，用 `-Repository` 指回本
worktree 构建与运行；本仓的台架一行没改。场景 `rehearsal/b10-05-map26-activation.ps1` 与配置 `rehearsal/b10-05-map26-activation.setup.psd1`
放在这里供复现，不是台架场景。台架只在本机回环上起服务端、假 RIoT、假 MesIngest 与合成车载端，不碰任何真设备。

- 假 RIoT 的 26 号图站点表就是上面读回的 216 个站；服务端完整确认的目录修订是 `3498438256227867560`，与按
  `TaskTypeStationCatalogEvidence` 的规则从 `catalog-26.json` 现算的一致（判据 B10-05-00）。所以演练里 `--catalog` 用的就是读回的那份文件，
  新鲜度检查是真过的，不是绕过的。
- 库从本票之前的 26 号图预置起步（只有 210、旧身份核对号），先用 `candidates/00-replicate-factory01-version-2.json` 激活出与 factory01
  10-10 相同内容的版本 2，再对本票候选 `scripts/parallel/task-type-stations.map-26.candidate.json` 走 README 的步骤。
- FieldOps 在服务端运行中对它正在用的库执行，与现场一样。

结果（`rehearsal/run4-green/`，11 条判据全 PASS；FieldOps 每一步的原始 JSON 输出在 `fieldops-transcript.txt`）：

| 步骤 | 结果 |
| --- | --- |
| 0 读现状 | 版本 1：210，旧身份核对号 |
| 1 复现 factory01 | `OK`，版本 2（210 + 217，旧记录号） |
| 2 本票候选 `--dry-run` | `PREVIEW`，0 违规；`changes` 恰好四条新增（218、219、220、221）；之后生效版本仍是 2 |
| 3 正式激活 | `OK`，版本 3 |
| 4 只读核对 | 生效版本 3，六条绑定与候选逐条相同（含六个新记录号），需求集六类，无暂停、无未结尝试 |
| 5 回滚到版本 2 | `OK`，生效的是新版本 4，内容与版本 2 相同 |
| 7 三份错误候选（正式激活） | 各自退出码 1、`REJECTED`，原因码分别恰好是 `TASK_TYPE_BINDING_AREA_NAMED_STATION`、`TASK_TYPE_STATION_REUSED`、`TASK_TYPE_BINDING_SITE_VERIFICATION_MISSING` |
| 8 再读 | 仍是版本 4，共 4 个版本，被拒的候选什么都没切换 |

**一个要写进操作步骤的发现**：预览的 `changes` 只在站号或站名变了时才列出一个任务类型
（`TaskTypeStationActivationService.Diff`），**只换核对记录号不算变化**。所以 210、217 换记录号在 dry-run 里看不见，要到激活后用只读动词核对。
新版本写入的是候选的整份内容，记录号确实换掉了（步骤 4）。

### 前三轮为什么没过（保留）

1. 第 1 轮：副本没带上一级 `scripts/*.psm1`（桌面锁等模块），台架启动前就报 `Import-Module` 失败。没有产生任何证据。
2. 第 2 轮：我改的测试少了 `using ControlServer.Domain;`，构建失败（`CS0103 AreaNamedStationName`）。没有进场景。
3. 第 3 轮（`rehearsal/run3-red/`）：FieldOps 的行为全部正确，红的是我场景脚本自己的两处错误——
   - 判据 B10-05-03 我原先写「六条变化」，实际四条，原因见上面「发现」；
   - 第一份错误候选被正确拒绝（原因码 `TASK_TYPE_BINDING_AREA_NAMED_STATION`，原文在 `run3-red/fieldops-transcript.txt`），但脚本从异常文本里
     取 JSON 时只认以 `{` 开头的行，而那行前面带着 `exited with 1: ` 前缀，于是取不到 `violations`，场景中止，后两份错误候选没跑。

   第 4 轮只改了这两处脚本，候选、预置与产品代码都没变。

## 文件

| 文件 | 内容 |
| --- | --- |
| `catalog-26.json`、`catalog-26.raw.json` | 2026-10-11 只读读回的 26 号图站点表 |
| `01-export-catalog-26.txt` | 导出脚本的输出 |
| `candidates/` | 演练用的辅助候选：复现 factory01 的版本 2，三份各带一处错误的候选 |
| `rehearsal/b10-05-map26-activation.ps1`、`.setup.psd1` | 演练场景与配置 |
| `rehearsal/run3-red/`、`rehearsal/run4-green/` | 两轮的判据与 FieldOps 原始输出 |
