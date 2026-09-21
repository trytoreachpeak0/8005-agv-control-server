# scripts/parallel —— 并行期 v2 在 factory01 上的第二套部署

control-server#262。约 2026-10-08 起 `factory01` 上同时跑两套 ControlServer：MVP 驱动 `agv01`，
这一套驱动 `agv02`／`agv03`，只吃注入的测试需求。权威文档是 `8005-workspace` 仓的
`remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md`，控制端入口是同目录下的
`scripts/19-deploy-control-server-parallel.ps1`。这里只放随产品版本走的部分。

## ⚠️ 实例定义里的地图还是占位值

`instance-factory01-v2.json` 里 `mapId` 是 **25**，那是 **MVP 那张图**。用户 2026-09-21 答复：
**MVP 跑 25，v2 跑 26**。

| 键 | 现值（占位） | v2 目标 | 出处 |
| --- | --- | --- | --- |
| `routeGraph.mapId`、`journeyRuntime.mapId` | 25 | 26 | 用户答复 |
| `journeyRuntime.mapIdentity` | `老厂前线new` | `老厂前线new_wk` | `evidence/field/2026-09-19-B6-map-name-baseline-check/real-riot/fields.json` |
| `journeyRuntime.dispatchZone`、`allowedDispatchZones` | `MAP-25-WIRE_TO_GATE` | 待现场取回 | 形式可推，没人确认现场真这么叫 |
| `journeyRuntime.admissionPolicyDeploymentId` | `MAP-25-WIRE_TO_GATE-20260827` | 待现场取回 | 无出处，日期是一次现场标定的产物 |
| 站点清单 `task-type-stations.settings.json` | 绑 25 | 要出 26 版 | 无 |

没有直接改成 26，是因为后三样没有出处，凭空编一组标识符格式正确、能过所有检查，错了也不会有
任何东西报警。**校验拦不住这一点**（25 是一个合法的正整数），所以**真装的第一步就是从 factory01
直查 RIoT 把这几样换成 26 的值**，在那之前不要装。

## 文件

| 文件 | 做什么 |
| --- | --- |
| `instance-factory01-v2.json` | 实例定义：端口、目录、服务名、车、RouteGraph、建单闸门 |
| `ParallelInstance.psm1` | 定义的校验、配置叠加、部署足迹。纯函数，自测覆盖的就是它 |
| `ParallelHost.psm1` | 读机器的辅助函数（MVP 服务指纹），安装与卸载共用 |
| `Install-ParallelInstanceLocal.ps1` | 在 factory01 上安装／升级／回滚 |
| `Uninstall-ParallelInstanceLocal.ps1` | 在 factory01 上按部署足迹逐项卸载 |
| `Start-FakeMesIngestResident.ps1` | FakeMesIngest 常驻的计划任务入口 |
| `Publish-FakeMesIngest.ps1` | 替身的 self-contained 发布（控制端跑） |
| `Test-ParallelInstance.ps1` | 自测，不碰任何机器 |
| `Invoke-ReverseCheck.ps1` | 在真实定义文件上做的反向验证，不碰任何机器 |

## 改完这里的任何东西之后

```bash
pwsh -File scripts/parallel/Test-ParallelInstance.ps1
```

```bash
pwsh -File scripts/parallel/Invoke-ReverseCheck.ps1
```

两者都要全绿。它们不在 CI 里（本仓 CI 跑的是 .NET 测试套件），所以没人会替你跑。

**孪生脚本**：MVP 那套的对应物是 `8005-workspace` 仓的 `remote-ops/factory-server/scripts/15-deploy-control-server.ps1`
与 `control-server/Install-ControlServerRemote.ps1`。两边刻意分开，所以一边的修复不会自己到达另一边——
改到机器层面的东西（清理通配符、防火墙规则、共享环境变量）时，去看另一边。
