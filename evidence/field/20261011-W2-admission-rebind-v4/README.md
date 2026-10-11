# 准入策略重绑到版本 4（2026-10-11 13:23）

## 为什么

用户把 26 号图上 16 个区号站长期改名（T04-01…08 → T4-1…8，T05-01…08 → T5-1…8；用户：MES 里写的就是 T4-1 这种，地图照 MES 改）。版本 3 绑定的是旧名，12:47:22 起每轮 2116，V2 以 `ADMISSION_POLICY_DRIFT` 拒收一切新需求。

## 做了什么（`01-v4-restart.txt`）

授权见 `00-authorization.md`。13:23:43 停 V2，停着时在 `appsettings.Production.json` 改四项后启动：

| 键 | 改前 | 改后 |
| --- | --- | --- |
| `RiotCreateDispatch.enabled` | true（因 idle-return 旅程关不上） | **false** |
| `JourneyRuntime.admissionPolicyVersion` | 3 | **4** |
| `JourneyRuntime.stationDepartureWaitTimeout` | 00:03:00 | **00:01:30**（用户定稿的测试值） |
| `JourneyRuntime.cargoHoldingTimeout` | 00:03:00 | **00:02:00**（用户定稿的测试值） |

`admissionPolicyDeploymentId` 不变（`MAP-26-WIRE_TO_GATE-20261007`）。改前备份 `D:\zhengyushao\control-server-v2-ops\cs566b\appsettings.Production.json.bak-20261011132344-before-v4`，sha 9F8CA065… → A6FBE240…。

## 回读

- `AdmissionPolicyState` Version=4，ContentHash 559a09b0…，13:23:47；`AdmissionPolicyAudit` 新行 Version=4，PreviousContentHash=2f07a058…（版本 3）。
- 绑定 205 站 × 6 类 = 1230 行，T 站为 T4-1…T4-8、T5-1…T5-8；站 222（「站点222」，不符合区号格式）不在内。
- 启动后 2116 为 0 条。V2 pid 16768，Manual，live 200；MVP pid 15624 未变。

## 要知道的

1. **这是直接改配置。**`D:\zhengyushao\control-server-v2-ops\installed-instance.json` 与仓库里的实例定义仍是 3。下次用 19 号脚本重装或升级时，定义会把它写回 3，库里已绑 4，存储层拒绝倒退，又是一次漂移。重装时必须带 `-AdmissionPolicyVersionOverride 5`（或更高；`scripts/parallel/README.md:290-300`）。仓库两份定义升版本由调度另开小票。
2. **区域归属表里没有 T 区号。**FieldOps 导入的那张表（217 行）全是 N 前缀，从来没有 T04-0x/T05-0x，所以没有要按新名重导的行；每区参数只有 WIRE 一行。但这也意味着：区号写成 T4-x/T5-x 的需求即使通过了准入，也会因区域表里没有该区号被当作范围外拒收（推断，`AreaScopeCriterion.cs:32-35`）。要接这些需求，得补区域表行，每个 T 区号前侧还是后侧须由用户给定。
3. 测试等待值收工改回 00:05:00 / 00:05:00（见 `../20261011-W2-test-waits/README.md`）。
