# control-server#325 的真装置证据

场景 `real-onboard-stale-stop-after-station-timeout`（本机真装置，调度 2026-09-27 批的时段）。每个目录是一遍运行，只留
`SUMMARY.md`、`assertions.json`、`timeline.jsonl`，三遍用作判定的另留 `snapshots/`（含协议故障代理的流量记录，SST-08 的「送达」
就读它）。组件日志（每遍约 9 MB）没入库，完整目录留在工作区 `scratch/cs325-rig/`。

## 六遍运行

| 目录 | runId | control-server | onboard | 结论 | 说明 |
| --- | --- | --- | --- | --- | --- |
| `run1-fixed` | `20260926T173432659Z` | `ed0fe4e4` | `4c2d2dc1` | FAIL | 场景脚本自己的错：读代理丢弃记录的 `messageType`，那个类型没有这个字段（StrictMode 抛）。第一趟 SST-00～05 已全绿 |
| `run2-fixed` | `20260926T173722053Z` | `19cfeb6e` | `4c2d2dc1` | FAIL | 窗口造出来了（SST-09 绿），第一下取消 `REJECTED`/`WORKLIST_REVISION_STALE`；之后「取消装货」没了，第二下按不到——脚本前提错了，改为车给几次按几次、迟到扫码另开第三趟。按钮没了的原因当时记成「车载端看到 STALE 就撤入口」，那是推的，而且错了：审查指出、代理记录证实，服务端答复取消之后 3～4 ms 把被丢的那张空清单原样重发（同一个 messageId），撤按钮的是它，见下文「审查之后的改动」 |
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

预期表里修复后三端写的车载端是 `b789c3d4`，实跑用的是 `4c2d2dc1`（跑之前 `ls-remote` 读到的 `w2g/fp-v2-impl` 顶端）。两者都含 hmi#199（合入提交 `1184bb0` 是两者的祖先，`git merge-base --is-ancestor` 核过），而且 `b789c3d4` 是 `4c2d2dc1` 的祖先，只是顶端前移了。

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
| SST-11 迟到取消之后撤按钮（预期表之后加的；撤按钮的是重发的空清单，见下） | 绿 | — → 绿 | — → **红** |

偏差（读代码，旧车载端 `65c86508`）：

- **SST-10 预期红、实际绿。**旧车载端的 `HandleSublotRejected`（`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs:2088-2107`）只在作业会话与清单号都相同时保留录入请求，否则撤掉。服务端的 STALE 拒收带的是收尾空清单的号，比车上那一版高，所以旧车载端收到它本来就撤录入。预期写错了，判据与产品都没问题。
  调度定：判据不动，定位改为「守服务端 STALE 带收尾那一版号」——它在③上红（服务端不答），在②上绿（旧车载端对号更高的拒收本来就撤）。车载端修复由第一趟 SST-03 判到，那一格在②上如预期红。
- **SST-04 预期红（推的）、实际绿。**旧车载端的「取消装货」跟着清单行走，空清单到车后按钮照样没了；只剩录入框还在（SST-03 红的就是这个）。
- **SST-08 预期「不确定」、实际绿。**旧车载端照样显示 `SublotRejectionReason`（原因码原样），服务端这一半本来就是新的。

结论：两端缺一不可。旧服务端红在 SST-03/04/05/07/08/10/11；旧车载端红在 SST-03（收尾之后仍能录入）。

## 审查之后的改动（PR #365 审查 5848653840，调度 09-27 转来）

上面六遍都跑在审查之前的脚本上（blob `18bd6388…`）。审查之后判据有增改，**正式证据是新 head 上的 CI `rig=real` 那一遍**，跑完补在下面；六遍本机运行留作对照与来历，不再改写。

改了什么：

- **SST-11 的归因改了，求值不变。**原先写「车载端据 STALE 认定本站已结束、撤掉按钮」，是推的，错了。读代理流量记录：run4 第二趟被丢的空清单 `997a3cfb` 在服务端答复取消之后 4 ms（01:51:20.751 → .755）原样重发（同一个 messageId、未丢），对照② 同样是答复之后 3 ms；撤按钮的是这张重发的收尾空清单。第三趟被丢的 `387791ee` 直到场景末尾断线前都没有重发，所以第三趟的撤录入确实来自车载端对 STALE 拒收的处理。
- **新增 SST-14**：每一条 STALE 拒收的 `currentWorklistRevision` 等于被丢那张收尾空清单的 `worklistRevision`（被丢的那行按 messageId 回发件箱取号）。这是「服务端 STALE 带收尾那一版号」的直接判据；SST-10 保留，但在修好的三端上它与 SST-08 同源，不是独立证据。旁证（读到的，run4 车载端日志与时间线）：第三趟车提交录入时带 `worklistRevision=5`，车载端日志记服务端拒收 `currentWorklistRevision=6`。被丢那张空清单的号本机没存（服务端库没进快照），要在 CI 那一遍里读。
- **新增 SST-13（第一趟前提）**：从 SST-01 到读 SST-03～05，`SessionHello` 行数与代理连接数不变、最后一条连接未关、车载端进程与主窗口还在。否则「不在」可能是断线清空投影或窗口没了。
- **SST-06 加严**：除 `SessionHello` 次数不变外，代理连接数不变、当前连接未关。
- **SST-09/12 窗口前提加一项**：这一趟丢了清单的话，必须恰好一张、是布下规则之后写进发件箱的空清单（丢错了会把到站那一版丢掉）。一张没丢也算窗口：旧服务端根本不发收尾空清单（对照③），车上照样挂着，那时迟到动作的判据照常求值、红在它们自己身上。规则这一趟没被用掉就 `reset`，不再带进下一趟——对照③ 里第二趟的规则就是这样带进第三趟、丢掉了第三趟到站那一版 `80b02802`。
- **SST-08 在撤录入的等待之后重数**「恰好一条」。
- 小项：`Invoke-CancelPress` 读答复载荷前先看属性在不在（StrictMode）；`Get-VehicleView` 的子批列表写成 `@(if ...)`。

新读法离线走过一遍（读到的）：用 run4 与对照③ 存下的代理快照按时间截断重放，run4 第二、三趟分别只认出 `997a3cfb`、`387791ee` 为本趟丢掉的清单，连接数与「最后一条未关」读得出；对照③ 第三趟认出「本趟一张没丢」。发件箱那一半（按 messageId 取号）本机没有存库，只能在 CI 那一遍上验。

### 正式证据：CI `rig=real`（`ci-36262963711/`）

| run | control-server | onboard | simulator | 结论 |
| --- | --- | --- | --- | --- |
| [`36262485596`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/36262485596) | `135169c4` | `4c2d2dc1` | `fb5f7c59` | 场景 PASS（214 秒），**但证据没传上去**：上传一步报 `Failed to CreateArtifact: Unable to make request: ENOTFOUND`，那一步 `continue-on-error`，作业仍是 success，下一步照常删了 vm01 上的证据目录。日志里不打逐条判据，SST-13/14 读到什么看不到，所以不作正式证据 |
| [`36262963711`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/36262963711) | `135169c4` | `4c2d2dc1` | `fb5f7c59` | **PASS，15 条全绿**，artifact `real-rig-evidence` 471995 字节，本目录是其中这一遍的 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`、`snapshots/` 与 `commits.json` |

四行核对（`36262963711`，读到的）：三端提交从场景那一步读，与上表一致；场景 PASS；`RIG_COMMIT_GUARD`/`RIG_DESKTOP_LOCK`/`RIG_DEADLINE`/`NOT_STARTED` 命中 9 行，全是源码回显；无停机（214 秒，整机已提交内存峰值 7.64 GiB）。

新判据第一次真跑读到的值（`assertions.json`）：

- **SST-13**：`SessionHello 1 → 1；代理连接 [#1 open] → [#1 open]；主窗口 在`。
- **SST-14**：拒收 `WORKLIST_REVISION_STALE rev=6 delivered=True`；第三趟被丢的空清单 `2a1dda61-2e32-e35f-acaa-7963c8fe746f` 号 6。第二趟被丢的是 `d6dad452…` 号 4，两趟的号不同，这一条比的是本趟那一张。
- **SST-09/12**：两个窗口各认出恰好一张本趟丢掉的收尾空清单（`d6dad452…`、`2a1dda61…`），规则都用掉了，没触发 `reset`。
- **SST-06**：按了 1 下，SessionHello 与代理连接都不变。
- **SST-10**：`canSubmit=False offersCancel=False listsSublot=True`。清单里仍列着这单是预期内的。读到的（本目录代理快照）：第三趟被丢的空清单 `2a1dda61…` 只有 02:38:19 那一行被丢，直到 02:38:25 场景末尾断线都没重发；第二趟的 `d6dad452…` 02:37:04 被丢、02:37:11 取消答复之后原样重发送达，与 run4、对照② 同一形状。推的：车载端对 STALE 拒收只撤录入请求与取消入口、不改清单行；SST-10 只判前两项。
