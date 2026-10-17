## 交接：今晚停在哪里，明早从哪里接着做（2026-10-14 20:45）

**今晚已完成**：方案第 16b～21 步。新库 B，生产段 s3.runtime-on 已装（运行时开、闸门关）。三车车载端都是 `d4957461`，接真 IO，逐仓核对都过，FieldOps 回读三车 `ready=true`。REQ-0198 两区各 65000。首趟开过一次闸，两条需求因现场没人，站点超时退回。20:38 关闸。

**当前状态**：
- 闸门关；区表 v2 只有表头，0 行。
- 两条已受理需求 `Q26096572-7`（agv02，STAGING_TO_WIRE，N2-4/2QHE71）和 `Q26097573-2`（agv01，WIRE_TO_GATE，N4-1/2QHE89）都在待派队列里，**各只剩 1 次站点超时**。
- 三车起栈、握手 READY，车载端一直开着：agv01 在 33，agv02 在 217，agv03 在 211 充电。
- 退回后没有重派，原因是区表为空（C11 读码确认）。

**证据**：控制端 `F:/8005-scratch/cutover-ev/production-20261014/`。逐仓核对记录在 `8005-workspace-v2/evidence/field/20261014-req0263-slot-verify/`（agv02 的 `run1`、`run2` 是操作失误，留作证据，没提交）。服务器端脚本在 `D:\zhengyushao\control-server-v2-ops\prod-20261014\`。

**明早顺序**（C11 已同意）：
1. 重跑 `20-check-production-io.ps1 -SshAlias agv01, agv02, agv03`。今晚的记录 02:13 过期，碰 agv01 前先问用户。
2. 和用户定那两条已受理需求怎么处理：现场有人有料，就让它们重派走完；否则接受作废，作废时会报人工搬运告警。
3. 导全量区表 `field-prep/area-assignments-merged-N-T.csv`：先离线比区名，再 dry-run，然后 `-Commit`。
4. 问用户开闸，带上新的 `io-check.json`。

**注意事项**：
- 用的 worktree：`F:/8005-wt/prod-cutover-8005-workspace`（HEAD `0e60c94`）、`F:/8005-wt/prod-cutover-f12bc809-8005-agv-control-server`、`F:/8005-wt/rig-req0263-8005-agv-control-server`。生产稳定后再删。
- W1 探针要从 PowerShell 或 Terminal 面板起。从 Git Bash 起的话，ssh 会截断 `-EncodedCommand`。
- 出发规则仍是旧的：装完后等 5 分钟修正窗口才走，没人扫码 5 分钟后退回。新规则由另一张卡片在做。
- 本卡片不关闭。
