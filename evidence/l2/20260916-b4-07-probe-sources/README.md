# control-server#71 本地验收：探针源码与前后对照结果

这里不是场景。票面规定本票不新建场景、不往 `l2.yml` 加行，而有几条验收标准要「一次本地运行证据」，所以写了几个
**临时探针**：跑的时候复制进 `scripts/l2/scenarios/`，跑完删掉，源码留在这里，谁都能照原样复现。

| 探针 | 证什么 | 证据 |
| --- | --- | --- |
| `b4-07-probe-slot-states` | `SlotStates` 把 1 号报成 `OCCUPIED`、2 号报成 `DISABLED`，服务端算出的可用仓变成 3～8，派出的目标仓是 `[3,4]`；`L2SlotGroups.psm1` 在真库上读分组、按组断言正例通过、反例判失败 | `../20260916-b4-07-probe-slot-states-001` |
| `b4-07-probe-area-assignments-off` | `AreaAssignments = $false` 时不导入，入库与绑定照做 | `../20260916-b4-07-probe-area-assignments-off-001` |
| `b4-07-probe-area-assignments-override` | 覆盖表时导入的就是覆盖内容，而且只导入一版 | `../20260916-b4-07-probe-area-assignments-override-001` |
| `b4-07-probe-real-with-slot-states` | `SlotStates` 与 `Onboard = 'Real'` 同给时编排器在启动任何进程之前报错 | `real-with-slot-states-001.console.log`（退出码 1，没有证据目录，因为什么都没启动） |

三个跑起来的探针都在提交 `0bff08c7` 上跑，`-BatchId batch-4`。

`sweep-before-72ff5cf6.results.json` 与 `sweep-after-0bff08c7.results.json` 是 `l2.yml` 清单里全部 17 个合成场景按清单的
`Runs` 各跑一遍的结果：改动前在 `fp/v2-impl@72ff5cf6` 的独立检出上，改动后在本分支 `0bff08c7` 上，两边都是 33 趟 33 PASS。
每趟的完整证据目录在控制端本机 `8005-workspace-v2/evidence/b4-07-20260916/` 下（`results.json` 里的 `evidence` 字段就是路径），
没有整体入库；入库的只有 `../20260916-b4-07-normal-load-001` 与 `../20260916-b4-07-emergency-stop-operator-release-001` 两份。

**带入 #84 之后又跑了一遍。**本分支用 merge 带入 `fp/v2-impl@f563efa8`（#84 的协议 v2.0.0 候选，改了假车载端的
`SublotSubmitted` 报文），并按审查意见去掉编排器里永真的派车判断、把 `OnboardPeers` 各项的 `SlotStates` 提前到启动前
校验，之后在 `7fbf1425` 上把同样 17 个场景重跑：`sweep-after-merge-7fbf1425.results.json`，33 趟 33 PASS。完整证据目录在
控制端本机 `8005-workspace-v2/evidence/b4-07-20260916/after-merge-7fbf1425/`。
