# 人工撤下一趟旅程（FieldOps）

**给现场人员与值班工程师看。**一趟旅程卡住、服务端自己放不掉、车上又没有货时，用这个入口把它撤下：旅程收尾，车上没装的
需求退回待派（会重新派车，不抑制），车回到可以接活的状态。

本入口由 control-server#771 落地（2026-10-19 用户定方案 D1～D6，方案全文在票上与工作区 `ds-tasks/cs771-design.md`）。
实现在 `src/ControlServer.Host/Runtime/Withdrawal/JourneyWithdrawal.cs`，命令行在 `tools/ControlServer.FieldOps/Program.JourneyWithdrawal.cs`。
**这是第一部分，只有服务端停着时直接写库这一种走法**；服务端在线的接口、看板上的提示是第二部分。

## 什么时候用

两类卡车，车上都没有货：

- **车载端离线、旅程挂在半路**（10-18 agv01）：车低电或断电，旅程停在开往取货站途中，重建待发或停住。
- **装货结果未知、车上没有出口**（10-19 agv03，hmi#370）：扫了码、下了装货命令，结果以 UNKNOWN 收尾，服务端
  `Blocked/LOAD_RESULT_REQUIRES_RECOVERY`，车载端 RecoveryRequired；车上的恢复开关关着（用户定不打开），屏上没有能按的出口。

**车上有货不能用**（D2）：现场看见仓里有篮子，或车报的读数说有货、读不出，一律拒绝，改走车载端的恢复会话（18 号开窗）。

## 撤下会做什么

全在一次保存里：

- 那一次结果未知的装货结成「已取消」（与对上的补偿、取消同一个终态）；之后车再报那一次的结果，服务端只记历史。
- 旅程上每一条还没装的需求退回待派：不抑制，等待年龄不重置，下一轮派车按「已释放待改派」重新受理。
- 旅程收尾，收尾码 `WITHDRAWN_BY_OPERATOR`。车上的空清单说「本站因任务释放结束」（`DEMAND_RELEASED`）。
  租约、订单占用、用途占用一起放。
- 写一条管理员审计（`JOURNEY_MANUAL_WITHDRAWAL`），记操作人、理由、两条核实记录、仓位事实、篮子去向和整份计划。
  被拒的请求也记。

撤下之后 20 号关闸、开闸都不会再因这趟旅程被拒（关闸只数没完成的旅程，开闸只查没完成的旅程名下的单）。

**它不做的事**：不取消 RIoT 上的单（所以要先确认没有活单）；不动 MES；不动车载端的日志簿。车载端那条待答的 UNKNOWN 结果，
在车接到下一条仓位命令时，由车自己先核实门关着、再覆盖掉（车载端的既有设计）。

## 先决条件

1. **v2 服务停着。**撤下动词先探服务端的健康检查，有应答就拒；再拿服务进程持有的那把库锁，拿不到也拒。停服务会停掉挂在
   别的车旅程上的故障监看，所以先停受理、等其他车收完手上的旅程。
2. **RIoT 上这辆车没有未结束的订单**，现场看过，记下截图或记录的位置（`--riot-verification`）。
3. 结果未知的装货那一类，还要：
   - 现场到车前看过那个仓：空的、门锁着（`--slot-facts 5=EMPTY_LOCKED`，多个仓用逗号分开，要恰好是那次装货的目标仓）；
   - 那一篮现在在哪里（`--cargo-location`，例如「在机台上」）；
   - 车最近一次报的安全摘要里，没有仓位读不出、门没锁、开锁输出没复位这三样。车没停稳不挡撤下（那是开锁前的事）。
4. 这辆车没有开着的异常恢复会话（有就先用管理员关闭入口关掉）。
5. **车载日志簿里的几项**（onboard-hmi#372 / PR #376 的结论：撤下之后，车要在下一条仓位命令时自己覆盖那条待答记录，靠的是下面这几项都成立）。
   服务端看得见的，入口自己查；看不见的，现场读车载日志簿：

   | 项 | 谁来核 | 怎么核 |
   | --- | --- | --- |
   | a. 车报的未结操作就是要撤的那一次 | 入口自己查（车最近一次恢复报告），不一致拒绝 `VEHICLE_REPORTS_ANOTHER_UNSETTLED_ATTEMPT` | 演练输出的 `sessionUnsettledAttempt` 应等于 `unresolvedLoad.slotOperationAttemptId` |
   | b. 开锁记录里的门关好、锁反馈读得到 | 入口查车最近的安全摘要（有门读不出、没锁、输出没复位就拒）；演练输出列出 `sessionActiveUnlockSlots` | 摘要可能是旧的（离线写法时服务端停着），现场再看一次锁反馈；读不到的要先修好或复位那路反馈，否则撤下后车回 READY、下一条命令却一直被拒 |
   | c. 日志簿里没有恢复向量，发件箱里没有被放弃的恢复结果 | 服务端看不见 | 读日志簿（见下）：恢复状态 `recoveryVector` 为空；发件箱里没有 `AbandonedReasonCode` 为 `BUSINESS_ID_CONTENT_CONFLICT`／`MESSAGE_ID_CONTENT_CONFLICT` 的恢复结果行。有的话先由维护人员在车上「人工核对后结束此恢复」 |
   | d. 没有强制隔离 | 服务端看不见 | 读日志簿：`forcedIsolation` 为空。有的话，下一趟的命令不能落在它的「物理状态不明」仓位上，交工程师 |
   | e. 发件箱里未确认的行 | 入口保证：车补发那条待答结果时服务端只确认、不改动（用例 `TheVehiclesReplayOfTheUnknownResultAfterAWithdrawalChangesNothing`） | 读日志簿时记下未确认的行。除了那条待答的操作结果，别的未确认行交工程师看 |

   **怎么读日志簿**：`19-archive-onboard-journal.ps1 -SshAlias <车> -Suffix check-<日期> -CheckOnly -EvidencePath <新目录>`（`-Suffix` 必填，
   只判定时不会用到），只复制、只判定、不改名，结果在 `journal-check.json`：里面有未结操作、开锁记录、恢复向量和未确认的行（a、b、c、e）。
   它不抽强制隔离，d 要看它复制下来的日志簿副本（`<新目录>/read`）里恢复状态的 `forcedIsolation`。它要求车载客户端停着，所以放在窗口里、停 v2 之后做（停车载客户端用 `10-start-onboard-stack.ps1 -Stop`）。
   它报 `JOURNAL_OUTBOX_PENDING`、`JOURNAL_UNLOCK_ACTIVE` 不是撤不了，是列出上面 b、e 两项要看的东西。不想停客户端时，可以照 onboard-hmi#370
   的做法只读复制日志簿再读；屏上有没有「人工核对后关闭」入口也可以用 `18-read-hmi-screen-state.ps1` 只读读屏看（只能看 c 的一半）。

会话原因码是 `OPERATION_RECOVERY_REQUIRED` 还是 `PENDING_FACT_RECONCILIATION_REQUIRED` 都不影响撤下：车重连之后，它报的那次没结的操作留在
待核实事实里，原因就排成后者，底下是同一件事。撤下之后那次操作结成取消，两个码都会消失。

## 步骤

先演练，看清楚再撤。演练只读，服务端在跑也能做：

```powershell
dotnet ControlServer.FieldOps.dll plan-journey-withdrawal --database <v2 库文件> `
  --journey <JourneyId> --vehicle-key <RIoT VehicleKey> `
  --operator <工号> --reason <理由> --site-verification <现场核实记录> --riot-verification <RIoT 核实记录> `
  --slot-facts 5=EMPTY_LOCKED --cargo-location 在机台上
```

输出里逐项核对：旅程、车、每条需求（都应是 `RELEASE_TO_BACKLOG`）、那一次装货与车报的逐仓读数、这一趟名下的单。记下
`confirmationCode`。然后停 v2 服务，撤下（参数同上，加探测地址与确认码）：

```powershell
dotnet ControlServer.FieldOps.dll withdraw-journey --database <v2 库文件> --probe-server http://<v2 健康地址>/ `
  <同上的参数> --confirm <confirmationCode>
```

`outcome` 为 `WITHDRAWN` 即成。之后启动 v2 服务（或直接用 20 号关闸，它会以闸门关着的状态重启服务）。车重连后收到收尾快照，
会话不再因那一次装货而 RecoveryRequired；车要回到 Ready，还要它自己停稳（`MODE_AUTO`、`MT_FINISHED`）。

演练之后状态变了（例如车掉线、又来了一条录入），撤下会以 `CONFIRMATION_STALE` 拒绝，并给出新计划与新确认码：重新核对再按。
同一个确认码按第二次答 `ALREADY_DONE`，什么都不再写。

## 拒绝码

| 码 | 意思 | 怎么办 |
| --- | --- | --- |
| `OPERATOR_REQUIRED`／`REASON_REQUIRED`／`SITE_VERIFICATION_REQUIRED`／`RIOT_VERIFICATION_REQUIRED` | 必填项没填 | 补上 |
| `JOURNEY_NOT_FOUND`／`VEHICLE_MISMATCH` | 旅程号不对，或车不是这一趟的车 | 核对旅程号与 VehicleKey |
| `JOURNEY_ALREADY_COMPLETED` | 这一趟已经结束 | 不用撤 |
| `JOURNEY_KIND_NOT_WITHDRAWABLE` | 空闲返回或充电旅程 | 它们有自己的收尾，不走这里 |
| `CARGO_ON_BOARD` | 这一趟有已装上或正在装的另一篮 | 车上有货，走车载端恢复会话 |
| `OTHER_OPERATION_UNRESOLVED` | 除了阻塞的那一次，还有别的仓位操作没结 | 交工程师 |
| `SLOT_FACTS_REQUIRED`／`CARGO_LOCATION_REQUIRED` | 结果未知的装货要现场仓位事实和篮子去向 | 到车前看过再填 |
| `SLOT_FACTS_MALFORMED`／`SLOT_FACTS_DO_NOT_COVER_THE_TARGET_SLOTS` | 仓位事实写错，或没盖住那次装货的目标仓 | 照演练输出里的目标仓填 |
| `SITE_REPORTS_CARGO` | 现场说仓里有货 | 走车载端恢复会话 |
| `SLOT_EVIDENCE_MISSING`／`SLOT_EVIDENCE_NOT_EMPTY` | 车没报过那一次的结果，或报的读数不是空、锁着、输出复位 | 现场与车对不上，交工程师 |
| `DOOR_NOT_PROVEN_CLOSED` | 车最近的安全摘要说有门读不出、没锁或输出没复位，或车从没报过 | 先让门证明关着 |
| `VEHICLE_REPORTS_ANOTHER_UNSETTLED_ATTEMPT` | 车最近报的未结操作不是要撤的这一次 | 撤下之后车仍挂着它，交工程师 |
| `RECOVERY_SESSION_OPEN`／`SLOT_FAULT_DECLARATION_OPEN` | 这辆车有开着的恢复会话，或这一趟有未了结的判故障声明 | 先把它们了结 |
| `REBUILD_RECORD_OPEN` | 这一趟有还在等的自动重建 | 第一部分不处理，交工程师（了结重建记录随后补上） |
| `ORDER_OUTCOME_UNKNOWN` | 这一趟有一张建单结果未知的单 | RIoT 上可能有活单，先查清 |
| `FAULT_IN_EFFECT`／`EMERGENCY_STOP_OPEN`／`FAULTED_CARGO_BOUND` | 车的故障在效、急停没解除，或有故障货物绑定 | 先走故障清除与急停解除 |
| `CONFIRMATION_REQUIRED`／`CONFIRMATION_STALE` | 没带确认码，或演练之后状态变了 | 重新演练 |
