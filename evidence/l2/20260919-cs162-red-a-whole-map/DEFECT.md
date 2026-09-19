# 红证据：缺陷版本 whole-map

注入的缺陷：暂停只按 mapId 落：看板暂停 STAGING_TO_WIRE 时把同图全部任务类型一起暂停（TaskTypeHoldEndpoints）。

缺陷提交 `72877d99` 在本地临时分支 `tmp-cs162-red-whole-map` 上，基于 `d223b14e`，未推送、用后删除。
判定与失败原因见同目录 `SUMMARY.md` 与 `assertions.json`。
