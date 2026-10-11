# F 段（桩 211 充电）与 E 段（空闲返回）——cs#616 包重跑，2026-10-11 晚 agv02

服务端 `0c396ba7`（cs#616 审查通过、CI 并行时部署，依据见 `../20261011-W2-prep5-cs616/00-basis.md`），车载端 `eced7c16`，新库 newdb4。授权见 `00-authorization.md`（用户原话「确认 agv01 不去 211，可以开始」）。

## 结论

**F、E 都走通，全程没有 00002，没有人工处置。**下午这两步都因为 upperId 过长（69／71 字符）被 RIoT 以 `00002` 拒单（cs#616）；换成短编号后同样的目的站一次就建单成功。

| 段 | 事实 | 出处 |
| --- | --- | --- |
| F 去桩 | 16:44:57 开闸；16:45:08 2205（92 < 95，临时策略 v2）→ 2240 分配 211 → 16:45:10 RIoT 确认 `W2G-CHARGE-62d6bd7c-20261011T084508103Z`（**39 字符**） | `04-gate-open.txt`、`05-watch-1645.txt` |
| F 充电 | 16:47:20 2257 证实到桩、预留转占用；2258 `CHARGING`（92%） | `07-log-arrival-charging.txt` |
| F 充满 | 16:50:57 2260 到 99%（完成线），周期 COMPLETE | `09-after-charge-complete.txt` |
| E 回等待点 | 16:51:00 2198 预留 216 → RIoT 确认 `W2G-IDLE-62d6bd7c-20261011T085100113Z`（**37 字符**）→ 16:53:02 2225 到 216、转占用；离桩时 211 释放 | `09`、`10-watch-idle-return.txt`、`11-exclusivity-after-e.txt` |
| 收尾 | 16:53:25 关闸 PASS；名册置空、策略回 v1、`windowCanClose=true` | `12-gate-close.txt`、`13-close-roster-policy.txt` |

## 两张单的 RIoT 原始记录（`14-riot-order-records-charge-idle.txt`，只读 `detailByOrderId`）

供「改图后、同一目的站、只差 upperId 长度」对照：

| 单 | upperId（长度） | 任务 | 状态 |
| --- | --- | --- | --- |
| `order-2109203643214462976`，创建 16:45:10 | `W2G-CHARGE-62d6bd7c-20261011T084508103Z`（39） | move 26/212 → move 26/211 → act 78（开始充电，参数 1） | 5（完成） |
| `order-2109205118850301952`，创建 16:51:02 | `W2G-IDLE-62d6bd7c-20261011T085100113Z`（37） | act 78（参数 2）→ move 26/216 | 5（完成） |

下午失败的对照（同目的站 211／216，upperId 71／69 字符，`00002`）在 `../20261011-00002-investigation/` 与 `../20261011-W3-S1-charging/`、`../20261011-W2-idle-return/`。

## 现象

- 行驶途中会话 `RECOVERY_REQUIRED`（`DEPARTURE_UNSAFE`），到站恢复，已知现象。
