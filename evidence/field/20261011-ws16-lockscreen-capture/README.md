# ws#16 取屏脚本：锁屏时取屏验证（2026-10-11 20:04，agv02）

脚本：8005-workspace 分支 `feat/ws16-hmi-screen-capture` @ `b55d108`（detached worktree `F:/8005-scratch/ws16-capture`），`remote-ops/onboard-hmi/scripts/17-capture-hmi-screen.ps1 -SshAlias agv02`。只读、不点击、不输入。

条件：两车静止在等待点（agv02 在 216、agv03 在 215），没有在途单，闸门关；用户在 agv02 车载电脑上按 Win+L，本会话选「已锁屏」后运行。用户当时看到的屏：锁屏画面。

| 项 | 结果 |
| --- | --- |
| 结果码 | `DESKTOP_LOCKED`，退出码 3（预期结果，不是失败） |
| 预检 | `console: HT session 1; locked: True`；`logonUi: true`；没有残留任务与其他取屏工具 |
| `desktop` 字段 | `locked: true`、`logonUiInSession: true`、`inputDesktop: "Default"`（ws#16 会话预计会是安全桌面，实际读到 Default，照实记） |
| 整屏图 `-screen.png` | 取到了，内容是**锁屏画面**（风景图与 20:04 时钟），没有车载端界面 |
| 车载端窗口图 `-window-1.png` | **取到了完整的车载端窗口**：上层会话就绪、在等待点3 待命、发车安全允许发车、8 个仓已锁空仓、操作记录 |
| UIA 文字 `-uia.json` | 有车载端文字（`visibleTexts`），含行程计划「等待点3 已到达」与各仓状态 |
| 启动器回报 | `TASK_STARTED lastRunTime=2026/10/11 20:04:04 lastTaskResult=0x41301` → `LAUNCH_DONE`；收尾 `RUN_DIRECTORY_REMOVED`、无残留任务 |

结论：锁屏时整屏只能截到锁屏画面，但车载端窗口图与 UIA 文字都能取到。

> 入库说明：整屏图 `agv02-20261011T200419-screen.png`（约 1.5 MB）与 `agv02-20261011T200419-uia.json` 已在 workspace 仓库 ws#16 分支入库（`remote-ops/onboard-hmi/evidence/ws16-20261011/`），这里不重复存放。
