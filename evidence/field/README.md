# 现场窗口证据

现场窗口的证据放这里，一次窗口一个目录。**窗口号分属两条互不相干的线，别混**：

| 线 | 窗口号 | 内容 | 采集脚本 |
| --- | --- | --- | --- |
| 仓位配置就绪 | `W1` 车辆资格、`W2` 多车与等待点、`W3` 自动充电 | 三台车逐仓 IO 核对、门禁逐台启用 | `scripts/field/Invoke-W1FieldWindow.ps1` |
| 装卸站收敛语义（ADR-cross-0058） | `FW-SC1` 三个操作员不作为场景、`FW-SC2` 完整闭环一趟 | 开门不放料 / 仓门已闭超时 / 仓门未闭超时 | `scripts/field/Invoke-SlotConvergenceFieldWindow.ps1` |

**`W1` 与 `FW-SC1` 都读作「窗口一」，但不是一回事**——前者是车辆资格，后者是装卸站语义。目录描述里写清
楚是哪一条线。

目录形状两条线一致：

```
evidence/field/<日期>-<窗口号>-<描述>/
  SUMMARY.md        结论、身份、逐台结果、判据、照片指针、效力边界
  assertions.json   机器可读判据，含现场记录原文
  timeline.jsonl    一行一次动作，只追加
  logs/             每次工具调用的 stdout 与 stderr
  snapshots/        每一步的状态快照与不可改写审计导出
```

## 为什么不复用 `evidence/g3/`

**G3 是门禁，现场窗口不是门禁。**放同一个目录会让「切片通过」与「现场验收通过」混淆——前者说的是
一段代码在合成装置下的行为，后者说的是三台真车上的八个仓位被人逐个按过。两者都可能绿，但它们证明
的不是同一件事，而混在一起之后没有办法再分开。

## 纪律

四条与 L2 完全一致：

1. `-EvidenceRoot` 必须是**不存在**的目录。
2. **红的证据不允许被绿的重跑覆盖。**
3. 跑失败时 stage root 不删。
4. **证据目录只增不改。**要纠正就写一个新目录，并在新的 `SUMMARY.md` 里指向被纠正的那一份。

## checkpoint：装卸站收敛那条线多一层

`FW-SC*` 的证据目录**在窗口进行中就开始写**，一次 `-Checkpoint` 一个 `snapshots/<序号>-<label>/`，
最后一次 `-Finalize` 才算判据、写 `SUMMARY.md`。这不是为了方便，是因为有些判据事后读不出来：
「站点期限到期后再等 20 分钟依然不结束」说的是一个会被结算本身覆盖掉的时刻，关门那一下就没了。

于是纪律第 1 条在这条线上的说法是：**第一次 `-Checkpoint` 时 `-EvidenceRoot` 必须不存在，之后必须存在**。
只增不改照旧——checkpoint 不改写更早的 checkpoint，`-Finalize` 拒绝覆盖已有的 `assertions.json`。

## 无人值守：谁敲 checkpoint、谁写现场记录

2026-09-11 起验收不要人开关仓门、放东西、按 HMI（8005-agv-program#43）。车照走真实线路，IO 接车上的
slots-simulator，于是 `FW-SC1` 由 `scripts/field/Invoke-SlotConvergenceFieldDrive.ps1` 一条命令跑完：

- 操作员的每一个动作由 `scripts/field/FieldOperator.psm1` 经 ssh 调车上两个 loopback 面完成；
- checkpoint 由它在动作说「就是这一刻」的时候敲（`c-deadline-reached` 是告警挂上那一刻，`c-plus-20min`
  是期限之后 20 分钟、关门之前，`b-settled` 是旅程离站之后）；
- 现场记录由它按实际做了什么写出，`ioKind = SIMULATOR`、`drivenBy` 记驱动的 runId；照片与观察人不适用，
  `SC1-W-01` 在这种记录上改判「记录由驱动脚本写出」，`SC1-A-05` 读车载端快照的 `availableRecoveryActions`；
- 最后自己 `-Finalize`。

它**不派车**（`-Dispatch` 仍要每次在对话里授权）、**不关旅程门**（`13-close-gates-when-idle.ps1` 要同时跑着）。
同一组动作在 L2 上的彩排是 `real-onboard-field-window-rehearsal`，那里的采集器按 `-MinimumHoldMinutes 3`
判，`SUMMARY.md` 会写明「只能算彩排」——**彩排目录在 `evidence/l2/` 下，不进这里**。

模拟器证明的是软件闭环，不是光幕极性、锁反馈时序与机械弹开；`SUMMARY.md` 按 `ioKind` 照实写。

## 完整数据库不进这里

`FW-SC*` 读的是 factory01 上的**生产库**，里面装着与本窗口无关的旅程，而 `evidence/` 进 git。
所以证据目录里只有本窗口那几个 demand 的行；完整库落在仓库之外的 `~\w2g-stage\field\<窗口>-<runId>\`，
`SUMMARY.md` 里留 runId 作指针。`run-demand-bearing-g3-vectors.ps1` 的 `-FieldRunRoot` 要的就是那个形状。

## 照片

**照片本身不进 git。**现场记录里留的是指针（相对路径或归档系统里的编号），照片归档在
`remote-ops/` 之外的现场资料库。指针指向的东西找不到，是现场记录填错了，不是证据格式的问题。
