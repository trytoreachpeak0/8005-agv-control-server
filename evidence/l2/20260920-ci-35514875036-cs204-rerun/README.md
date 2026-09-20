# CI 真装置重跑 run `35514875036`（control-server#204）

七次运行**全部 PASS**：`real-onboard-expected-action-overdue` 3×14/14、`real-onboard-durable-ack-lost`
3×10/10、`real-onboard-restart-while-waiting-operator` 7/7。

## 四行核对

| | |
| --- | --- |
| 服务端 | `4e84616743b989a7c1a29a71660e1e1765c0b6cb`（**本票最终 head**） |
| 车载端 | `24af41e4ab769f10cd15381fd3b4a56babb62c78` |
| 模拟器 | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| **这一遍真跑起来了** | 没有 `RIG_COMMIT_GUARD`／`RIG_DESKTOP_LOCK`／`RIG_DEADLINE`；判据条数 14／10／7，场景都走到了底 |

三端提交取自每个场景自己写的身份表，七次一致。

## 这一遍的绿证明不了什么

上一轮 `35513390399` 里 `real-onboard-durable-ack-lost` 三遍中有一遍红（服务端在握手窗口里插了一条
`SlotOperationCommand`，机理与归属见 `../20260920-ci-35513390399-cs204/README.md` 与 control-server#259）。

**在 1/3 的命中率下，这一遍三次全绿有相当概率是运气。它不代表那个缺陷不在了，只代表这三遍没撞上。**
它的作用是「本票的改动没有引入新问题」，**不是**「那条握手窗口已经安全」。那条窗口的归属在
control-server#259（服务端要装的那道门）与 onboard-hmi#50（握手期间收到非应答报文该怎么办的设计问题），
两边互链。

## 入库了哪几份

完整目录三份，每个场景各一份（`durable-ack-lost-01`、`expected-action-overdue-01`、
`restart-while-waiting-operator-01`）；**七次都跑了**，七份 `.log` 摘要都在。同一场景的第二、三遍与
`_stage` 未入库。

这一次是在入库**之前**就定好留哪几份的——上一轮先整份入库再删，blob 已经进了历史，删除只动工作树、
省不了仓库体积。那个教训写在 `../20260920-ci-35513390399-cs204/README.md` 里。
