# 红探针结果（运行之后写）

运行：`run-01/`，控制台输出 `run-01.log`。`outcome FAIL`，退出码 1。

**与预期一致：只有 `L2-MSO-10` 红，红在「先前后后」。**十二条里其余十一条都是 PASS。

`L2-MSO-10` 的实际值：卸货顺序 `FRONT,REAR,FRONT`（甲、丙、乙，也就是加入旅程的先后）；丙的卸货命令早于乙的卸货提交；
采样里丙那一仓（下标 9）先于乙那一仓（下标 11）开。三处都与撤掉 cs#303 之后的推理一致。
`L2-MSO-11`（全程一次一扇）仍 PASS，说明撤掉的只是跨需求的先后，一次一扇没受影响。

## 一处偏差，不影响判定

`identity.onboardHmiCommit` 是 `27a82b38`，不是事先写的 `ecdb3a0b`：这次运行启动后，我在同一个车载端 worktree 上提交了
`ONBOARD_HMI_G2` 证据（`27a82b3`），运行在那之后才读 `HEAD` 并发布了它。`git diff --stat ecdb3a0b 27a82b3 -- . ':!evidence'`
为空，两者的车载端产品逐字相同。模拟器 `fb5f7c59`（detached worktree）。服务端 `d259898d`。

探针用的 detached worktree `rig-b7-18-cs-no303` 跑完已删除；`d259898d` 从未推送。
