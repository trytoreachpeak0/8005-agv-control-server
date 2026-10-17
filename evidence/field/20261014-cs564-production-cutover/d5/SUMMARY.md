# 2026-10-17 v2 生产实例换装 v4（cs#564）总结

现场工作会话写，调度 C11。时刻一律为 CST。本目录 `d5/` 是这次换装的全部证据。

## 结论

v4 包已经装到 factory01 的 v2 实例上，三车车载端也都升到了 v4：

- 服务端：a799c998（run 38880005621，protocol-v4.0.0，APPROVED_RELEASE）
- 车载端：c9d24dfd

数据库换了一个新的空库，14:11:53 开闸。用户 17:2x 确认「算 v4 验证成功了」，关门提示这一项按他的决定跳过。

## 为什么换新库（cs#740）

13:30 第一次换装失败：新包一启动就崩，报 `OUTBOX_PROTOCOL_IDENTITY_MISMATCH`。原因是服务端 `ProtocolOutbox` 里留着 9 行 v3 身份的未确认消息（`SublotEntryRequested` / `SublotRejected`）。19 号脚本自动回滚到了旧二进制，迁移没有落库。

用户决定换新库继续换装，调度定的是方案 A。证据：`03-v4-deploy.txt`、`04-start-fail-diag.txt`、`05-server-outbox-unacked.txt`、`06-unacked-full.txt`。

## 方案 A 的步骤与时刻

| 时刻 | 步骤 | 证据 |
| --- | --- | --- |
| 13:30:36 | 停服，整目录备份到 `D:\zhengyushao\control-server-v2-ops\db-backup-pre-v4-20261017-133036` | `02-stop-backup.txt` |
| 13:38:48 | 旧库改名为 `C:\ProgramData\8005\ControlServer.V2.pre-v4-20261017`（库 SHA 9238983F…），新建空目录，ACL 与原来一致 | `10-newdb.txt` |
| 13:40 | 19 号脚本按 s1 单车定义装 v4，自动迁移到最新（含 cs#725）；JourneyRuntime 关，RiotCreateDispatch 关 | `11-v4-deploy-s1.txt`、`12-v4-s1-readback.txt` |
| 13:44 | 在 a799c998 上 publish FieldOps，传到 `fieldops-a799c998`，两端哈希一致；只读验证通过 | `13`、`14` |
| 13:47 | 导入事实：seed（车型 c2590b03）、三车 bind-io、等待点 214/215/216、充电桩名册、充电策略 v1（导入、批准、激活）。指纹与 10-15 一致 | `15-facts-dryrun.txt`、`16-facts-commit.txt` |
| 13:53 | 逐仓核对：用户决定跳过重核、用过去的数据，把库 C 的重提记录改成新车型 id 后 verify，三车都 READY | `18-verify-v4-reattest.txt` |
| 13:58 | 19 号脚本按 s3.runtime-on 部署：admissionPolicyVersion=6，stagingDepartureRulesEnabled=True，dispatchGeneration=100；任务类型站点预设自动激活（e642f23c） | `19-s3-whatif.txt`、`20-s3-deploy.txt`、`21-s3-readback.txt` |
| 13:59 | 补导每区参数（fc98bae1）。在 s1 定义下导入会被拒（DISPATCH_ZONE_NOT_FOUND），所以放到 s3 之后 | `17`、`22` |
| 14:00–14:06 | 三车依次停栈；用 19-archive 把日志簿归档到 `8005AGV.pre-v4-20261017`（pending=0、loadsOnBoard=0）；06 号部署 c9d24dfd，两个端口都传，IO 仍连真模块。agv01 这一步先问过用户 | `30`～`41` |
| 14:06–14:07 | 三车起栈，状态都是 READY（用户授权，现场有人） | `42-*-start.txt`、`43-readiness.txt` |
| 14:08 | 20-check，3/3 都在真模块上 | `44-io-check.txt`、`io-check.json` |
| 14:09:45 | 导入区表 EB80BC22，315 行，读回 aa1e990d（不含 T8-8） | `45`、`46` |
| 14:11:53 | 开闸（用户授权，现场有人）。开闸后的意图全部是 DispatchGeneration=100，没有认领旧单 | `48-gate-open.txt`、`49-intents.txt` |

## 开闸后的运行情况

- agv03：14:20 前后电量塌到 8%，进入保护，上位机断电。用户把车推上 211 并重新定位后，充电单在充电状态下被派出，同站直接 SUCCESS，v2 的周期进到 CHARGING。80% 时周期 COMPLETE。但车载程序在上位机重启后没有自己起来，车也就没离桩。15:21 起栈后，它立即接了一单搬运离桩，211 当场改派给 agv01。证据：`51`、`54`、`55`、`56`。
- agv02：15:01 塌到 8% 后关机，用户推上 211 充电。16:38 起栈后一直不被派单，原因是 v2 充电周期外充上桩的死锁（cs#744）。用户结束充电后，下一轮就派出了，17:09 起送 47。证据：`63`、`64`、`66`、`67`。
- 第一单真货：agv03 从 140 取 1 篮，16:2x 送到 220。agv02 从 217 取 Q26102447-7，17:09 离站送 47，到站完成。
- 现场大量用「取消装货」，代表这批货已经被人工运走。这些业务键被永久抑制（CANCELLED_BY_OPERATOR，REQ-0155）。MES 不关单的话，同站会一直补下一条，直到补完为止。
- 三车掉电都在 25% 附近突然塌陷：待机约每 6.5 分钟掉 1%，到塌陷点后几分钟内掉到 8～9%。强制充电线 30% 离塌陷点只有大约 5 个点，要不要调策略由调度另外问用户。

## 必测清单（program#184）

| 项 | 结论 | 证据 |
| --- | --- | --- |
| 机台出发往返 | **通过**。16:09 第一次按出发被拒（WORKLIST_REVISION_STALE：取消触发 revision 31，出发请求带的是 30，相差 62 ms）；16:13 第二次带 revision 31 被 ACCEPTED，持货中离站 | `60-agv03-140-load-departure-rejected-then-accepted.txt`、`61` |
| 待送点倒计时（R3） | **通过**：装上第 1 篮 1.3 秒后开始计时，时长 5 分钟，到期 0.7 秒后建卸货单离站。「新需求刷新」这一项**没有样本**（追加的那一条与开始计时同一时刻） | `67-agv02-cargo-holding.json` |
| 待送点按条取消（R4） | **通过**：只终结那一条，车上已装的那一篮仍是 LOADED。入口无法从日志区分，按代码推断是清单行取消（hmi#353） | `66-agv02-217-cancellations.txt` |
| 关门提示 | **跳过**（用户决定） | — |

更正记录：`57` 原来被我记成「机台出发」，实际是同站截短离站，已改名并加了更正说明。

## 今天新开的票与登记

- cs#740：服务端发件箱残留 v3 未确认行，导致升级崩溃（换新库的起因）
- cs#741：充电周期撤回后，留下一条从未建单的孤儿意图，状态一直是 PENDING_RECONCILIATION。目前没有挡住 agv02
- cs#744：v2 周期外充上桩的车被永久挡在派车之外；backlog 的原因被后评估的车覆盖；06:08 CHARGING_ROUTE_GRAPH_UNAVAILABLE
- program#172：调度登记了三条待议的业务规则，其中一条是 STATION_TIMEOUT_ANCHOR_NOT_RELEASABLE（锚定需求超时后不能单独退回，直接转人工）。另外两条见 program#172 原文
- 交给送全检调度、并入 cs#742 看的：出发应答慢（5 秒和 10 秒）、2292 与 2139 的离站期限口径、取消后停靠仍为 PENDING

## 遗留状态

- 区表 v1 是 aa1e990d，**不含 T8-8**。Q26069Y01-1 是 09-02 的陈旧需求，还挂在 MES 里；调度定了先不导 D0414E28。
- 旧库 `C:\ProgramData\8005\ControlServer.V2.pre-v4-20261017` 原样留着。
- 13:30 的备份 `D:\zhengyushao\control-server-v2-ops\db-backup-pre-v4-20261017-133036` 也留着。
- 三车的旧日志簿都在 `C:\Users\HT\AppData\Local\SQCD\8005AGV.pre-v4-20261017`。
- worktree `F:/8005-wt/prod-v4-a799c998-8005-agv-control-server` 等证据 PR 合入后再删。旧库、备份和旧日志簿，删之前都要先问用户。
- factory01 上的 `fieldops-a799c998` 保留，现场运维要用。
