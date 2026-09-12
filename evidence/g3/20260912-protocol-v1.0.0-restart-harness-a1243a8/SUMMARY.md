# `G3`（进程重启）：已发布的 `protocol-v1.0.0` 上 `FP-IS-00`／`06`／`14`／`15` **全部 `PASS`**

`STAGED_G3_PROCESS_RESTART_PASS`，25 条断言全绿，`failedAssertions` 为空；四片 `formalSlicePass: true`。本轮没有带 `-Slice`。

**它取代 `../20260912-fp-is-14-15-restart-harness-58f1a49/`，作为进程重启 runner 这四片的现行证据。**那一份绑的是协议候选 `16e2567`，原样保留。
重跑原因同 `../20260912-protocol-v1.0.0-staged-harness-a1243a8/SUMMARY.md`：协议已在 `9f22db8` 上发布为 `protocol-v1.0.0`，两端已换成已发布身份。

## 绑定

commit 绑定照旧从 `scripts/run-staged-g3.ps1` 的 param 块读回。

| | commit |
| --- | --- |
| `controlServer` | `6b21662c60a2e13aaf86043b146d3d886cf91dd1`，三个阶段的 `serverBuildCommits` 一致 |
| `onboardHmi` | `c86bac5eaec57c36351f7d45b458deaa42fde22e`，三个阶段的 `onboardBuildCommits` 一致 |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| `runner` | `22f0852b839b84b3257976fc268ba7bd9bb5bdb5`，`runnerWorktreeCleanAtStart: true` |

目录名写的是 `a1243a8`，runner 实际记录的是 `22f0852b`。两者的 runner 代码相同：`22f0852b` 只是在 `a1243a8` 之上提交了主 runner 的证据；
跑之前核对过 `git diff a1243a8 22f0852b -- scripts src tests` 为空。

## 实读

**`FP-IS-14` 拒绝路径**：

| 时刻 | 库里的事实 |
| --- | --- |
| phase 1 | 激活 `ACTIVATED`，version 2（对照组） |
| phase 2（篡改车上生效配置，车载端进程重启） | 会话代 2，`RecoveryRequired`／`SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`；第二次激活 `FAILED`，version 3 |
| phase 3（服务端重启） | 会话代 3，原因码不变；生效配置仍是 version 2 |

**`FP-IS-15` 车重启后告警采纳**：

| 观测点 | 告警投影行 |
| --- | --- |
| phase 2 之后 | 会话代 2，序号 3 |
| 全程结束 | 会话代 3，序号 5 |

两条告警断言（采纳重启后的快照、之后不回退）都是 `PASS`。与 `16e2567` 那一轮逐项相同。序号为什么不是 1，以及「序号回到 1 仍被采纳」
是由会话代推出来的、并非直接观测到，原因同那一份 SUMMARY 的说明。

## 未在本轮证明的

- 看板渲染不在这个 runner 里，看板显示全部告警由 L2 `onboard-alarm-snapshot-dashboard` 证明。
- 篡改过的车没有被修回来；恢复规程还没有门禁阶段。
