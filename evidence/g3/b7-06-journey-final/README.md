# 这一轮 journey G3 的结论，只留 `run-result.json`

**`JOURNEY_G3_PASS`**：14 个场景全 PASS、114 条断言 0 失败、`secretLeakFiles` 为空，
六个切片（`FP-IS-01`／`02`／`03`／`07`／`10`／`11`）逐个 PASS。

三端：服务端 `686efae1c8ac50de434e1bddaeef9ae7770e768b`、车载端
`08569c4f2f83a8f13b734d161055351c2621537a`、模拟器 `fb5f7c593742bf98bc3957b8729a38aad5321f28`，
协议 `86575456c847041515b7b75e8851a00e0d939804`。

## 这一轮是自检，不是门禁证据

`commits.controlServerCommitSource` 与 `commits.onboardCommitSource` 都是
**`SELF_CHECK_OVERRIDE`**。G3 的提交绑定写死是有意的设计，由出口票的第 1 步统一移动；
一张票为自己跑一轮就改它，等于替别的票决定了它们认证哪一版车载端。

所以这一轮证明的是**这份代码能过**，**不是那六个切片的 G3 已经有了门禁证据**。
`classification` 里 `fullG3` 与 `releaseCandidate` 都记着 `INCONCLUSIVE`——完整 G3 要四个
runner 都跑，本票的资源口径只包含 journey runner。

## 为什么只有这一个文件

整份证据目录 19M（同类 G3 证据目录是 662K），大头是每个场景 1.4–2.1M 的服务端 SQL 日志。
自检运行按 `-SelfCheck*` 的参数文档不作门禁证据提交，所以目录没有入库——**但结论本身
应当可核，而不是只能信一句转述**，于是 `run-result.json` 单独留下。它带着上面全部数字。

**注意 `evidenceFiles` 里那 795 条记录指向的文件没有入库**，它们连同目录一起删了。
那份清单留在这里只说明当时产出过哪些文件、各自的 SHA-256 是多少，不要拿它去校验什么。

第一轮白跑的红证据在 `../b7-06-journey-relpath-inconclusive/`，那一份是完整的。
