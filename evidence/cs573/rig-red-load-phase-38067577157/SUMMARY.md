# cs#573 修复前真装置：清单搅动下装货进度丢失（run 38067577157）

结论：**FAIL，红在装货阶段**，不在场景当时的判据上（判据表为空）。这是本票缺陷的另一个后果，作为「装货段修复前红」的证据入库；离站那一路的修复前红见同目录上一级的另一份证据。

## 身份

| 项 | 值 |
| --- | --- |
| CI | `l2.yml` `rig=real`，run [38067577157](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38067577157)，runner `win11-01-control-server-desktop` |
| 服务端 | `18271d26f9aba54c93bf5492c53beeba938b4e3b`（分支 `fix/cs573-red-baseline`：反做实现 `6498c580`，投影仍是今天的严格读法） |
| 车载端 | `535c94fce47a10879ba1f404d04f603ba1a65bbf`（`w2g/fp-v2-impl`） |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main`） |
| 场景 | 当时版本的 `real-onboard-departure-under-listing-churn`：车一到取货站就打开假 RIoT 的 `faults/nonfinal-listing-churn`（补 250 张、每 10 次读搅 4 次） |

## 发生了什么（读到的，`onboard-agv-20261011.log`，车载端本地时间 +08:00）

| 行 | 时刻 | 内容 |
| --- | --- | --- |
| 28 | 00:31:24.907 | 向 1 号仓发开锁脉冲 |
| 31–34 | 00:31:25.226 | 收到 `SessionReadiness`：`RECOVERY_REQUIRED [DEPARTURE_UNSAFE]`——投影读不全回了未知 |
| 36 | 00:31:25.830 | `Warning` 「仓位操作进度未能发送，不影响仓位判定：attempt=6841c16e-…，phase=WAITING_OPERATOR，round=0，error=InvalidOperationException。」 |
| 43 起 | 00:31:29 起 | 会话每 5～10 秒在 `RecoveryRequired` 与 `Ready` 之间来回 |
| 47–175 | 00:31:32–00:33:24 | 共 33 行「忽略并发重复SlotOperationCommand：attempt=6841c16e-…」——服务端在重发同一条装货命令，车载端按并发重复丢掉；`WAITING_OPERATOR` 再也没有补发 |

服务端日志不逐条记录重发，所以重发的证据是车载端收到并忽略的这 33 行。`db-StationOperations.json`：这条装货停在 `Prepared`、`CommittedAt` 为空。`timeline.jsonl`：场景等服务端收件箱里的 `WAITING_OPERATOR`，120 秒超时。

## 两个结论

1. **本票（cs#573）**：清单搅动让投影频繁回未知，会话随之频繁未就绪；修复前，装货中的进度上报会撞上这个窗口。修复后投影不闪，这条路不再被触发；修复后的装货段由场景 `real-onboard-load-under-listing-churn` 判。
2. **车载端另一个缺陷（调度另开车载端票）**：会话不是就绪时 `OperationProgress` 被拒，之后不补发；服务端只能干等超时。不论投影为什么闪，进度丢了就没人补。
