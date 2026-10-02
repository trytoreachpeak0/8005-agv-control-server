# G3-13-27 对照第 1 遍：hmi#242 之前的车载端，预期红、实际红

- 时间：2026-10-02 10:02～10:04 UTC，笔记本真装置（调度放行，两遍对照的第 1 遍）。
- 三端提交：control-server `d4ab2742`（cs#410 头）、onboard-hmi `68af85360f8899a9e5ff3ee5d142900ae2c46709`
  （`w2g/fp-v2-impl` 顶端，不含 hmi#242）、slots-simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。
- 结果：FAIL，只有 G3-13-27 红，其余六条（21～26）全绿。

G3-13-27 先等清桩中业务状态被车载端确认（G3-13-24，10:03:31），再在随后 3 秒里读界面结果一行 `UnableToChargeStatus`：
8 次读数全是空串（`timeline.jsonl` 里 `hmi-unable-to-charge-status-held`）。车载端日志第 23 行记下它收到了 `outcome=Confirmed`、
`MANUAL_CHARGING_HOLD`——收到了，但用途转为 `CLEARING_MAINTENANCE` 时把结果清掉了。这正是 hmi#242 要修的行为，说明这条断言抓得住它。

入库前删掉了 `logs/` 与 `snapshots/`，保留两端日志片段（保留原行号）。
