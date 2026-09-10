# 现场窗口一重跑：只开了窗、起了车载端，没有进现场（未 finalize）

结论：**未执行**——三个场景一个都没跑，本目录不会 finalize，也不会再往里追加。

对应地图票 [现场窗口一](https://github.com/trytoreachpeak0/8005-agv-program/issues/19)。
上一次实跑是 `../20260910-FW-SC1-operator-inaction/`（包 `20260910T022619Z`，卡在 #37）。

## 身份

| 项 | 值 |
| --- | --- |
| 包 | `20260910T121022Z` |
| 服务端 | `56a06ef` |
| 车载端 | `3cf2665` |
| 协议 | `protocol-v0.3.0` |
| 车 | agv01 `老厂前线新多仓位1`，IO `192.168.71.150:502`（真实模块） |

## 两帧

| 帧 | 时刻 | 内容 |
| --- | --- | --- |
| `snapshots/01-00-ready` | 2026-09-10 23:51 | 恢复窗口已开（`onboardRecoveryResumeEnabled=True`），车载端 `-NoSimulator` 起来后会话 `generation=415 Ready`；未完成需求 0 |
| `snapshots/02-client-vanished` | 2026-09-10 23:59 | 客户端进程已不在——**用户经 VNC 手工关闭**（23:55:57 自控制端 `172.19.162.241` 的 VNC 会话，无崩溃事件）。服务端会话行仍是 `Ready`，`health/ready` 仍 200 |

之后恢复窗口已 `-Revert`，两端复核关闭。

## 读这个目录要知道的

- **第二帧里的 `Ready` 不代表车在线**：`SessionRecoveries` 存的是最后一次握手的结论，`UpdatedAt` 停在 23:49:57，客户端关掉后服务端没有改写它。
- 下次开窗**新开目录**。采集器的包 runId 守卫对同一个包会放行追加，所以这一条靠人守。
