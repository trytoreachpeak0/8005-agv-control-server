# 第二轮 journey G3：全绿

- 运行：2026-09-30 07:52:44～08:18:25 UTC，`run-journey-g3.ps1` 自检覆盖（`SELF_CHECK_OVERRIDE`），控制端 `91523c0a`、车载端 `f3c3e939`、模拟器 `fb5f7c59`、协议 `86575456`。
- 结论 `JOURNEY_G3_PASS`，`formalSlicePass` 为 true（`assuranceLevel` 是 `JOURNEY_SIMULATED_COUNTERPARTS`）。16 个场景全部 PASS；切片 FP-IS-01、02、03、07、08、10、11、12 全部 PASS。
- 本票新场景 `g3-waiting-point-idle-return` 的 7 条断言（G3-12-01～07）全部 PASS。
- 与第一轮（`evidence/g3/cs390-journey-fa4a5ce3`，红在场景脚本）相比，只改了场景脚本 G3-12-07 那一行的读法。

## 已知的一处说明文字瑕疵

`G3-12-04` 的 `actual` 文字把两条腿显示成了 `[1 2:BUSINESS BUSINESS@…]`：场景里拼说明文字的 `Format-Plan` 写的是 `@(Get-PlanLegs …)`，又包了一层数组。判据本身用的是直接赋值的 `$latestLegs`、逐条腿判断，不受影响。随调度看板上那张「`@(Invoke-L2Query …)` 类包装数组」的票一起改，本票不改，好让 G3 与最终 CI 跑在同一份代码上。

## 为什么整目录入库、它能不能按清单校验

整目录入库只是和 2026-09-22 那份 journey 证据（`evidence/g3/20260922-protocol-v2.0.0-journey-82bfa415`）的做法一致，没有更强的理由。**这份证据不能按 `run-result.json` 里的清单逐个校验 sha256**：仓库的 `.gitattributes` 没有给 `evidence/g3/**` 关换行转换，入库时文本文件（日志、json）的 CRLF 被转成了 LF，检出后的字节与清单记下的不同。这个缺口已报调度、登在看板上，恢复派工时开票；本票不改。
