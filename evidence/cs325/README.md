# control-server#325 的真装置证据

场景 `real-onboard-stale-stop-after-station-timeout`（本机真装置，调度 2026-09-27 批的时段）。每个目录是一遍运行，只留
`SUMMARY.md`、`assertions.json`、`timeline.jsonl`，三遍用作判定的另留 `snapshots/`（含协议故障代理的流量记录，SST-08 的「送达」
就读它）。组件日志（每遍约 9 MB）没入库，完整目录留在工作区 `scratch/cs325-rig/`。

## 六遍运行

| 目录 | runId | control-server | onboard | 结论 | 说明 |
| --- | --- | --- | --- | --- | --- |
| `run1-fixed` | `20260926T173432659Z` | `ed0fe4e4` | `4c2d2dc1` | FAIL | 场景脚本自己的错：读代理丢弃记录的 `messageType`，那个类型没有这个字段（StrictMode 抛）。第一趟 SST-00～05 已全绿 |
| `run2-fixed` | `20260926T173722053Z` | `19cfeb6e` | `4c2d2dc1` | FAIL | 窗口造出来了（SST-09 绿），第一下取消 `REJECTED`/`WORKLIST_REVISION_STALE`；之后修好的车载端撤掉「取消装货」，第二下按不到——脚本前提错了，改为车给几次按几次、迟到扫码另开第三趟 |
| `run3-fixed` | `20260926T174256892Z` | `6c88311c` | `4c2d2dc1` | FAIL | 只红 SST-08 的「车已确认」：真车载端对 `SublotRejected` 不回 `DurableAck`（车到服务端只有 `SnapshotAppliedAck`），发件箱 `AcknowledgedAt` 在真车上恒空。改为代理送达＋提示区显示 |
| `run4-fixed` | `20260926T174908175Z` | `fd1d40be` | `4c2d2dc1` | **PASS** | 13 条全绿 |
| `control2-new-server-old-onboard` | `20260926T175256232Z` | `fd1d40be` | `65c86508` | FAIL（预期） | 对照②：旧车载端（hmi#199 之前） |
| `control3-old-server-new-onboard` | `20260926T175646578Z` | `ba9cb2d4` | `4c2d2dc1` | FAIL（预期） | 对照③：旧服务端（cs#323 之前） |

模拟器六遍都是 `fb5f7c59`。run1～3 的红是场景脚本的错，逐次修在下一遍的提交里；产品没有改动。

## 三遍判定运行用的是同一个脚本（调度 09-27 要求）

run4、对照②、对照③ 的场景脚本逐字相同，run1～3 期间的修改都已含在内：

| 文件 | `fd1d40be` 里的 blob | 对照③ 用的副本（`git hash-object`） | SHA-256（两者相同） |
| --- | --- | --- | --- |
| `real-onboard-stale-stop-after-station-timeout.ps1` | `18bd63886088a94098fa97d0f3e0364198ecd8ee` | `18bd63886088a94098fa97d0f3e0364198ecd8ee` | `f2893b8856ed07251264b06ad3fd8d2390f688a8c8b3efd864b6d073bb42a5cb` |
| `real-onboard-stale-stop-after-station-timeout.setup.psd1` | `179cdd49b075a2cd17e2558b69b5587219581d73` | `179cdd49b075a2cd17e2558b69b5587219581d73` | `ae7937da8664f576f6aa0b88e3b3bcdd6bb590ae2579ae041c5d7cc93ef717f8` |

时间线（UTC）：
- run1～3 各在自己的提交上跑：`ed0fe4e4`（17:33:14 提交）、`19cfeb6e`（17:37:21）、`6c88311c`（17:42:56）。
- `fd1d40be` 17:49:01 提交；run4 17:49:08 开跑，`SUMMARY.md` 记 `controlServerCommit` = `fd1d40be`。
- 对照② 17:52:56 开跑，同一个 worktree、同一个提交（`SUMMARY.md` 记 `fd1d40be`）。
- 对照③ 的脚本副本 17:53:09 从这个 worktree 复制（`scratch/cs325-l2copy/`，复制后 `diff -r` 无差异），17:56:46 开跑；副本的 blob 与 `fd1d40be` 相同（上表）。

读到的：上表的哈希与各 `SUMMARY.md` 的提交号。推的：run4 与对照② 跑时 worktree 没有未提交改动——`SUMMARY.md` 只记提交号、不记工作区是否干净；这之间我没有改过这个 worktree，跑完之后 `git status` 为空，下一次改动是跑完之后的 SST-10 定位改写。

**跑完之后脚本只改了文字**：SST-10 的描述与文件头「哪一条证哪一端」一段（调度定，见下）。判据的求值没有动，`git diff fd1d40be -- scripts/l2/scenarios/` 只有注释与描述串。CI `rig=real` 那一遍跑在最终提交上。

## 对照：预期与实际

预期表是跑之前写的，原样保留在 `01-predictions-before-runs.md`（2026-09-26 17:32:59Z 最后修改，早于 run1 的 17:34:32Z；SHA-256
`5cd2bb0153a153b5c7b913f949dd9b7fe74531ddaa82213bf0127da7e89d3797`）。预期是在第三趟与 SST-11、SST-12 加上之前写的，表里没有这三条。

### 实际结果与偏差说明

| 判据 | 修复后（run4） | ② 新服务端＋旧车载端：预期 → 实际 | ③ 旧服务端＋新车载端：预期 → 实际 |
| --- | --- | --- | --- |
| SST-03 不再要子批 | 绿 | 红 → **红** | 红 → **红** |
| SST-04 不再给取消 | 绿 | 红（推的）→ **绿**，偏差 | 红 → **红** |
| SST-05 清单不挂这单 | 绿 | 绿（推的）→ 绿 | 红 → **红** |
| SST-06 按取消不掉线 | 绿 | 绿 → 绿 | 绿 → 绿（按了两下，都没掉线） |
| SST-07 取消都以 STALE 拒绝 | 绿 | 绿 → 绿 | 红 → **红**（两下都 `ACTION_NOT_ALLOWED_IN_STATE`） |
| SST-08 迟到扫码 STALE 拒收并显示 | 绿 | 不确定 → **绿** | 红 → **红**（没有答复） |
| SST-10 STALE 之后撤录入 | 绿 | 红 → **绿**，偏差 | 红 → **红** |
| SST-11 STALE 之后撤按钮（预期表之后加的） | 绿 | — → 绿 | — → **红** |

偏差（读代码，旧车载端 `65c86508`）：

- **SST-10 预期红、实际绿。**旧车载端的 `HandleSublotRejected`（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:2088-2107`）只在作业会话与清单号都相同时保留录入请求，否则撤掉。服务端的 STALE 拒收带的是收尾空清单的号，比车上那一版高，所以旧车载端收到它本来就撤录入。预期写错了，判据与产品都没问题。
  调度定：判据不动，定位改为「守服务端 STALE 带收尾那一版号」——它在③上红（服务端不答），在②上绿（旧车载端对号更高的拒收本来就撤）。车载端修复由第一趟 SST-03 判到，那一格在②上如预期红。
- **SST-04 预期红（推的）、实际绿。**旧车载端的「取消装货」跟着清单行走，空清单到车后按钮照样没了；只剩录入框还在（SST-03 红的就是这个）。
- **SST-08 预期「不确定」、实际绿。**旧车载端照样显示 `SublotRejectionReason`（原因码原样），服务端这一半本来就是新的。

结论：两端缺一不可。旧服务端红在 SST-03/04/05/07/08/10/11；旧车载端红在 SST-03（收尾之后仍能录入）。
