# cs#754 证据：受理只预留数量，扫码后按同侧最小空仓分配

## L1

- `l1-red-c238a202.txt`：新用例在实现之前的提交 c238a202 上全红（8 条），原因都是受理时已定死仓号。用户例子那一条：`Expected: [1, 2]`，`Actual: [3, 4]`。
- `l1-round1-485db935.txt`：本机定向第 1 轮（调度时段 02:06～02:10），仓位相关 85 个类 2220 条，2200 过、20 红。20 红全是测试写死了「受理时就有仓号」的旧口径，逐条改写见 PR #764 正文；改完后 75 条定向复跑全绿。

## 合成 L2（本机第 2 轮，02:24～02:46，经 Invoke-HeavyLocal）

| 场景 | 结论 | 说明 |
| --- | --- | --- |
| `slot-assignment-after-scan-1` | PASS | 新场景。甲被站点期限结掉（TERMINATED，没下过装货命令，仓号 []），乙扫码后分到 [1,2]，装货命令与装货记录也是 [1,2] |
| `slot-group-selection-1` | FAIL | 新加的等待写成 `@(Get-CommandSlots …).Count`，恒为 1，扫码前就放行，SGS-09 读到 []。脚本错误，不是产品回归 |
| `slot-group-selection-2` | PASS | daddeb4c 只改那一行等待为 `(Get-CommandSlots …).Count`，判据未动 |
| `slot-group-temporarily-full-1` | PASS | |
| `staging-to-wire-slot-group-1` | PASS | |
| `area-assignment-version-freeze-1` | PASS | |
| `mixed-side-station-two-trips-1` | PASS | |
| `sublot-rejected-after-entry-1` | PASS | |
| 5 个 `same-direction-*-journey-1` | PASS | 共用的 `SameDirectionJourneyCommon.ps1` 改过 |

每个目录只留 `SUMMARY.md` 与 `assertions.json`；完整证据（日志、快照、时间线）在控制端 `F:/8005-scratch/cs754-l2/`。
