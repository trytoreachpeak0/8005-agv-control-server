# cs#573 方向 C（当场重读 + 时间预算）的真装置证据

修法：车载端读的 vehicle-safety 投影在全厂非终态单分页读不全时，同一次请求里当场重读（默认再读 2 次），
整次读取限在 2 秒预算内（小于车载端 3 秒的请求超时）；车载端放弃请求时服务端记 499。

全部是 CI 真装置（`l2.yml`，`rig=real`），车载端 `535c94fce47a10879ba1f404d04f603ba1a65bbf`，
模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`。场景见 `scripts/l2/scenarios/real-onboard-departure-under-listing-churn.ps1`。

每遍一个子目录，只留运行器原样产出的 `SUMMARY.md` 与 `assertions.json`。`rounds/<run>/` 是每一轮的作业汇总
（`job-summary.md`）、整机已提交内存采样（`commit-samples.csv`）与三端提交（`commits.json`）。日志与库快照留在
CI 的 artifact 里，只有闪烁的那一遍（`fix-38082575561-load-pause-03/`）另留了服务端与车载端的日志片段。

| 子目录 | 服务端 | 场景 | 结果 | 每遍开始时整机已提交内存 |
| --- | --- | --- | --- | --- |
| `red-38073432296-departure-01` | 对照 `8d1c8c9a`（投影只读一遍） | 离站 ×1 | **FAIL**：LC-01 搅 6 次；LC-02 没离站；LC-03 闪 5 次 | 11.09 GiB |
| `fix-38073442180-departure-01..03` | `27188049` | 离站 ×3 | **3/3 PASS**：搅 4、8、8 次；闪 0、0、0 | 6.68、4.73、4.24 GiB |
| `fix-38073442180-load-n4-01..03` | `27188049`（每 4 次读搅 1 次） | 装货 ×3 | FAIL，只红 LC-05（装货期间搅 3、3、2 次，门槛 5）；闪 0 | 4.01、3.84、3.84 GiB |
| `fix-38079050243-load-n3-01..03` | `2359608a`（每 3 次读搅 1 次） | 装货 ×3 | FAIL，只红 LC-05（搅 4、4、4 次）；闪 0 | 3.85、3.86、6.15 GiB |
| `fix-38082575561-load-pause-01..03` | `6372506d`（操作员停 10 秒、离站等待 60 秒） | 装货 ×3 | 第 1、2 遍 PASS；**第 3 遍红在 LC-02、LC-03**（闪 3 次，没离站） | 3.82、6.47、8.86 GiB（峰值 12.24） |
| `fix-38084870388-load-budget-01..03` | `c799c382`（加时间预算与 499） | 装货 ×3 | **3/3 PASS**：装货期间搅 10、10、10 次；离站等待期间 42、44、41 次；闪 0、0、0 | 3.87、3.87、3.86 GiB（峰值 7.53） |

## 38082575561 第 3 遍为什么红（读到的）

`fix-38082575561-load-pause-03/server-log-excerpt.txt` 与 `onboard-log-excerpt.txt`：

- 那一遍装置变慢，投影请求平均 1214 ms（第 1、2 遍 586、831 ms），超过 2 秒 15 次、超过 3 秒 8 次。
- 车载端读投影的请求超时是 3 秒（onboard-hmi `src/SQCD.Agv.Infrastructure/Configuration.cs:193`）。超时放弃后，
  服务端把取消记成 500（9 次，`TaskCanceledException: A task was canceled.`，栈里是 `ReadForOnboardAsync` →
  `ReadAllNonFinalOrdersAsync` → `ListOrdersByStatesAsync`）。
- 同一段时间车载端 `SafetyStateChanged` 发送超时、会话断开重连，服务端回 `DEPARTURE_UNSAFE`，离站等待被清零。

于是加了时间预算与 499（`c799c382`）。

## 最后一轮没有证明的事

`38084870388` 这一轮装置没有变慢（三遍开始时都约 3.87 GiB，投影请求最长 688、749、789 ms，没有一次超过
1 秒），所以时间预算在这一轮里从未截掉重读，也没有取消过任何读；服务端日志里没有 499 警告。它证明的是
「加了预算以后，正常速度下重读照常起作用」，**不证明慢装置上的行为**。慢的情形只由单测守：
`NoRereadStartsThatTheTimeLeftCannotCover`、`AReadStillRunningWhenTheBudgetRunsOutIsCancelledAndAnswersUnknown`、
`TheOnboardGivingUpIsLoggedAsACancellationNotAServerError`。

离站场景没有在 `c799c382` 上重跑：离站 3/3 那一轮（`38073442180`）跑的是 `27188049`，它到 `c799c382` 之间
`src/` 与 `tools/` 只多了预算与 499（中间两个提交只改装货场景的参数，离站那一路不变）。三遍读完所需时间在预算之内时，
代码路径与之前完全一样。
