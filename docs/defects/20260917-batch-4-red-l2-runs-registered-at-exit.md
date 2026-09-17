# 批次 4 期间四次 L2 红：出口时补登记

Status: 三项已修（两处场景脚本、`command-surface-order-hold`），一项修复待合入（假车载端应答缓存，PR #121）
Owner repository: `8005-agv-control-server`
Found by: 批次 4 各功能票的本机 L2 与 CI L2，见下表各行；本单由批次 4 出口票
[control-server#76](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/76) 在出口核对时补写

批次 4 出口要求「红证据全部保留，失败原因在 `docs/defects/` 有记录」。核对批次 4 期间留下的红运行时，
下面四次的失败原因只写在各自 PR 正文或 issue 评论里，`docs/defects/` 没有条目。本单把它们登记在一处，
诊断原样沿用当时的结论，没有重新定性。四次都**不是产品缺陷**：三次是 L2 场景脚本自己的错，一次是 L2
测试替身（假车载端）的缺陷。批次 4 期间另有一次红（CI run `35055524167` 的
`session-established-while-moving`）是产品缺陷，已有单独缺陷单
[`20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md`](20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md)，这里不重复。

## R-1 `slot-group-selection` 首跑：场景脚本两处错误

- 红证据：`evidence/l2/20260916-b4-09-slot-group-selection-001`（`controlServerCommit` `72dac836`），保留
- 失败原文：`Timed out after 90s waiting for: the FRONT demand was accepted and dispatched to the pickup station. Last observed: (nothing)`
- 原因（出自 [PR #111](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/111) 正文）：
  1. L2-SGS-01 把 `Invoke-L2Query` 的结果直接送进管道，实际值被拼成 `FRONT REAR`（`scripts/l2/README.md` 警告过的写法）。
  2. 第二条需求换了一个 EQP 号，而第一条已完成的需求仍在 MES 目录里，同一 AREA 对应两个 EQP，
     被 `AREA_EQP_NOT_UNIQUE` 挡住，等满 90 秒超时。
- 修复：同一 PR 内改脚本，两条需求用同一台机台的 EQP。之后 `-002`、`-003` 均 PASS。
- 超时之前的 7 条判据里，选仓相关的 6 条已经通过，所以这次红不涉及选仓逻辑。

## R-2 `structural-block-oversized-demand` 首跑：场景脚本把结果集包成单元素数组

- 红证据：`evidence/l2/20260917-b4-10-structural-block-oversized-demand-001`（`controlServerCommit` `e0a469ad`），保留
- 失败原文：`The property 'ReasonCode' cannot be found on this object. Verify that the property exists.`
- 原因（出自 [PR #115](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/115) 正文）：`Get-L2StructuralDispatchBlock`
  把整张结果集作为一个对象返回，脚本外面又套了 `@(...)`，得到「只有一个元素、元素是整张表」的数组，取 `ReasonCode` 失败。
  `slot-group-disabled-no-alert` 里同样的写法会让「无行」永远数出 1。
- 修复：`ee8ff207` 两个脚本一并改，之后 `-002` PASS（8/8），`slot-group-disabled-no-alert` 首跑 PASS（5/5）。

## R-3 `command-surface-order-hold` CI 第 3/3 次：断言早于假 RIoT 收到请求

- 红运行：CI run [`35109762084`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35109762084)，
  PR #105（control-server#72）的 `baffb2f3`；证据在该 run 的 artifact `l2-evidence`，未入库
- 原因（出自 control-server#72 的关闭评论）：场景在审计行出现后立即读取假 RIoT 的调用记录，此时
  `CMD_ORDER_HELD` 的 HTTP 请求还没到达假 RIoT。与 #72 的改动无关，调度会话同意只重跑失败作业，第 2 次尝试全绿。
- 修复：[PR #113](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/113)（已合入），L2-CS-08 改为等假 RIoT 收到请求后再断言。

## R-4 `mixed-side-station-two-trips` 单车两趟：假车载端按固定键缓存应答

- 红证据：`evidence/l2/20260917-b4-11-mixed-side-station-two-trips-red-001`（`controlServerCommit` `cd9023db`），保留
- 失败原文：`Timed out after 120s waiting for: demand B loaded and its journey reached the gate leg. Last observed: "Completed"`
- 原因（出自 [PR #121](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/121) 正文）：
  `tools/ControlServer.FakeOnboard/OnboardPeerSession.cs` 把 `SublotEntryRequested` 与 `PreDepartureSafetyCheck`
  的应答缓存在固定键 `sublot`、`safety-check` 下，键存在就原样重放。同一连接上第二趟拿到第一趟的 sublot，
  一台假车跑不完两趟。服务端行为正确。
- 当前处置：control-server#75 接受场景⑦暂时改为「两台车各一趟」（PR #120 合入 `0030aa2d`），三遍 PASS，
  证据 `evidence/l2/20260917-b4-11-mixed-side-station-two-trips-00{1,2,3}`。
- 修复：PR #121 把缓存键改为请求自己的业务标识，并把场景改回票面的单车两趟。**本单写成时 PR #121 尚未合入**，
  所以批次 4 出口三连跑的仍是两车各一趟的形状，见 `docs/batch-4-v2-exit-report.md`。
