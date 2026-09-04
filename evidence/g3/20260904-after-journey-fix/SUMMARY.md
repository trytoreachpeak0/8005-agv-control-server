# 2026-09-04 旅程死锁修复之后的 staged G3：三个 runner 各跑一次

## 结论

三个 G3 runner 在**当前两端身份**上各跑一次，全部通过，五十九条断言零失败：

| runner | 运行 ID | 结果 | 断言 |
| --- | --- | --- | --- |
| `run-staged-g3.ps1` | `20260904T042411158Z` | `STAGED_G3_RECOVERY_REPLAY_PASS` | 19 / 19 |
| `run-staged-g3-restart.ps1` | `20260904T042626824Z` | `STAGED_G3_PROCESS_RESTART_PASS` | 20 / 20 |
| `run-demand-bearing-g3-vectors.ps1` | `20260904T042849893Z` | `DEMAND_BEARING_G3_VECTORS_PASS` | 20 / 20 |

三个 `status` 与全部断言值都是 runner **自身发射**的机器可读结果，落在各自的 `run-result.json` 里，
本文档只是转述，不是判定来源。三次 `secretLeakFiles` 均为空数组。

## 为什么现在跑

`64e9bcc` 改了 `JourneyRuntimeEngine.AdvanceAsync` 的就绪门——会话进入 `RecoveryRequired` 之后，两条
不需要对端参与的停摆转移提到了门之前。恢复与重放正是 G3 三个 runner 覆盖的面，而上一次 G3 是
2026-09-01，绑的还是 TLS 期之后、明文候选身份的旧 commit。

## 冻结身份

| 组件 | commit | 来源 |
| --- | --- | --- |
| ControlServer | `4746ed27bfe0606238910bf31b493be2770395f7` | 当前 `ControlServer_MVP` |
| OnboardHmi | `f0465d9ad9f84607e3db972f1c6cb0ead910ab3d` | 当前 `origin/OnboardHmi_MVP` |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` | 当前 `origin/main`，未变 |
| protocol | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` | 与 tag 一致，未变 |

绑定改动是 `1d93ad1`，单文件两行；另两个 runner 通过 `Get-SharedCommitBinding` 从
`run-staged-g3.ps1` 取值，没有第二处副本。

**车载端绑的是 `f0465d9`，不是我方 `w2g/recovery-entry-mid-session-readiness` 的 `550dbe9`。**
`New-ExactClone -RemoteRef 'origin/OnboardHmi_MVP'` 要求该 commit 就是那条分支的 tip，而这也是对的：
`550dbe9` 只加测试，产品代码与 `f0465d9` 逐字节相同，G3 该证的是对方真正在发的东西。

## 本次不是什么

三次运行的 `classification` 均据实记为 `formalSlicePass: false`、`fullG3: INCONCLUSIVE`、
`releaseCandidate: INCONCLUSIVE`，四个正式切片（`W2G-IS-00` / `04` / `05` / `06`）保持
`INCONCLUSIVE`。staged G3 绑的是 commit、从 exact clone 重新 publish，**不碰任何候选产物**，也不构成
切片通过——一个切片通过要四道门全绿。

`run-demand-bearing-g3-vectors.ps1` 恢复的是一次已授权现场运行的库
（`C:\Users\szy\w2g-stage\run\fullloop-20260829T131549Z`，只读），那是真实需求写出来的真实状态；被测
的构建是绑定的 ControlServer commit，未必是当初写下它的那个构建。

## 同一轮的其他层

| 层 | 结果 |
| --- | --- |
| `ControlServer.Tests` | 301 通过 / 0 失败 / 0 跳过 |
| 车载端 `WireToGateG2Tests` + `UnitTests` | 28 + 109 通过 / 0 失败 |
| L2 合成四条 | 全 PASS |
| L2 真装置三条 | 全 PASS（`normal-load`、`clock-skew`、`recovery-entry-missing`） |
| CI（`4746ed2`） | `test` 与 `l2` 均 success |
