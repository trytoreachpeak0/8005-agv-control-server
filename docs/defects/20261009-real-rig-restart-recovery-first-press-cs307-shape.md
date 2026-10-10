# 真装置 `real-onboard-restart-with-open-recovery-session`：重启后第一次补偿撞上 cs#307 形状

Found by: CI run [`37953803085`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37953803085)，证据 [`evidence/l2/20261009-ci-37953803085-real-onboard-restart-with-open-recovery-session-01/`](../../evidence/l2/20261009-ci-37953803085-real-onboard-restart-with-open-recovery-session-01/)（control-server#393 出口真装置清单，31 次中 30 次 PASS）

## 现象（读到的）

判据要求车载端重启后第一次按「补偿清空」就完成补偿。实际是第一次被拒（`REFUSED`），再按一次才被接受。

- 00:13:25.67：车载端重启后握手成功，会话第 3 代。
- 00:13:29.69：车载端报：

  ```
  SafetyStateChanged发送失败，正在断开会话；待发项留着，重连后按此刻读数决定原样重发还是放弃。 | TimeoutException: The operation has timed out.
  ```

  意思是车载端发安全状态变化后等不到应答，超时，于是自己断开会话。下一行是：

  ```
  恢复向量LOAD_COMPENSATION未执行：reason=WIRE_TO_GATE连接已关闭。
  ```

  也就是这一次补偿因为连接已关闭而没有执行。
- 服务端同一秒记录 `Onboard connection ended with a protocol or transport error`，并带 `SocketException (10053)`，即对端中止了连接。
- 重连之后再按一次，补偿走完，工作流收敛。

## 归属

形状与还开着的 control-server#307 一致：服务端单条连接的处理循环停顿超过 2.5 秒，车载端等应答超时后自己断开（读到的，cs#307 标题；两边日志能对上）。这不是本批引入的，本批没有改这条循环。

## 是不是负载造成的

- **不是内存压力（读到的）**：这条红出现在 16:12～16:14Z，这段时间 vm01 已提交内存为 6.4～7.6 GiB，上限 16 GiB。整轮的最高值 11.63 GiB 出现在 15:45Z，也就是开头（`commit-samples.csv`）。
- **CPU 争用无法证明（推的）**：这一轮跑的时候，合成 `consecutive-all` 4 路（run `37953826544`）在同一台机器上同时运行。
- **空闲时三连全 PASS（读到的）**：合成那一轮跑完、确认五个仓都没有在跑的作业后，只重跑这一个场景三遍：run [`37963800400`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/37963800400)，3/3 PASS，三端提交相同，内存最高 7.19 GiB。

结论：连跑三遍绿，只说明这三遍没有撞上停顿，**证不了 cs#307 不存在**。cs#307 列入出口报告的剩余风险。
