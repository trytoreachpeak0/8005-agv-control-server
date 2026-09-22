# 红探针：撤掉 cs#303 后，后侧先追加的混挂站点场景是否红在「先前后后」

批次 7 出口 control-server#220。读的顺序：`00-expectation.md`（运行前写的预期）→ `00-diff-stat.txt`（探针提交只差 cs#303 的两个 src 文件）
→ `run-01/`（证据）→ `01-result.md`（结果与预期逐条对照）。

结论：与预期一致，只有 `L2-MSO-10` 红（卸货 `FRONT,REAR,FRONT`），其余十一条 PASS。

**车载端提交的偏差**：`run-01/assertions.json` 的 `identity.onboardHmiCommit` 是 `27a82b38af4921fdc967343ad0b8ce8e2cac5d9c`，不是预期写的 `ecdb3a0b`。
探针启动后，我在同一个被当作对端的车载端 worktree 上提交了 `ONBOARD_HMI_G2` 证据，运行在那之后才读 `HEAD` 并发布了它。
`git diff --stat ecdb3a0b 27a82b3 -- . ':!evidence'` 输出为空：两者在 `evidence/` 以外逐字相同，车载端产品一致，判定不受影响。
这次没出事只因为提交的恰好只有证据；被当作对端的 worktree 在运行结束前不应再提交。
