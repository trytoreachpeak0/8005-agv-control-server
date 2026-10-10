# G3-13-27 对照第 2 遍：hmi#242 的车载端，预期绿、实际绿

- 时间：2026-10-02 10:04～10:06 UTC，笔记本真装置（调度放行，两遍对照的第 2 遍）。
- 三端提交：control-server `d4ab2742`（cs#410 头）、onboard-hmi `8694d8fce1a54b42d35064470a0eeb3cd571f292`
  （`w2g/hmi242-unable-to-charge-result-kept`）、slots-simulator `fb5f7c593742bf98bc3957b8729a38aad5321f28`。
- 结果：PASS，七条（G3-13-21～27）全绿。

G3-13-27：清桩中业务状态被车载端确认（10:05:13）之后的 3 秒里，界面结果一行 `UnableToChargeStatus` 读了 11 次，全是 `CONFIRMED`
（`timeline.jsonl` 里 `hmi-unable-to-charge-status-held`）。与第 1 遍只差车载端提交，所以这一遍的绿归于 hmi#242 的修复。

入库前删掉了 `logs/` 与 `snapshots/`，保留两端日志片段（保留原行号）。
