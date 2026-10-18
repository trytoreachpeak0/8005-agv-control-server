# journey G3：协议 5.0.0 候选，cs#767 第 3 步（v5 出口）

- 时间：2026-10-18 12:16:35～12:46:31（CST），29.9 分钟，本机封锁时段内（看板持有者 cs#767），1 轮，未重试。
- runner：`scripts/run-journey-g3.ps1`，从 control-server `75e3c8d9`（PR #769 head）运行。
- 绑定：control-server `63d83a0f623a99d0260c2b1c95672a91584336b5`、onboard-hmi `a3a6753f6924b8f845483d6b886259c90af9b018`、slots-simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`、protocol `18d4fb3495033624cab412a7ee784f675dfe4cc2`（5.0.0 候选第三次修订，manifest `dd973918…`，`SUPERSEDING_CANDIDATE`，tag `protocol-v5.0.0` 尚不存在）。

## 结论

`JOURNEY_G3_SLICE_FAIL`，退出码 1。

- 20 个场景全部 `PASS`，包括本批改过、必须覆盖的两个：
  - `g3-load-cancellation-before-load`（cs#753：机台扫码前改按行内「取消」）；
  - `g3-multi-stop-plan`（cs#749：计划只含剩余停靠）。
- 156 条断言里只红 1 条：`protocolReleaseTagResolvesToTheBoundCommit`。九个切片（FP-IS-01、02、03、07、08、10、11、12、13）因这一条全部判 `FAIL`。
- `run-result.json` 的 `error` 为 `null`，`secretLeakFiles` 为空。

runner 最后的原文：

```
Journey G3 did not pass: JOURNEY_G3_SLICE_FAIL. Evidence: F:\8005-scratch\p767-g3\journey\evidence
```

## 那一条为什么红

`run-journey-g3.ps1:590`：

```powershell
$assertions['protocolReleaseTagResolvesToTheBoundCommit'] = $clonesVerified -and $protocolTagExists
```

journey runner 自首版（`4ae54d6ac`，2026-09-13）起就把「协议 tag 存在并指向绑定提交」当作正式通过的前提，不看 `approvalStatus`。staged、restart、demand-bearing 三个 runner 对候选身份是放行的（tag 不存在、且不声称 `APPROVED_RELEASE` 即可）。历史上每一轮 journey 都跑在已发布的身份上（b7-06、2026-10-14 的 v3.0.0、2026-10-16 的 v4.0.0），这一条一直是 `PASS`。

这一次绑定的是还没发布的 5.0.0 候选，`protocol-v5.0.0` 要等用户同意后才打，所以这条红是必然的，与产品行为和环境都无关。

## 之后怎么办

由调度定（见 cs#767）。建议：用户同意并打 `protocol-v5.0.0`（指向 `18d4fb34`）之后，在同一绑定上只重跑 journey 一轮拿 `JOURNEY_G3_PASS`，脚本不用改。
