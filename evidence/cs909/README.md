# control-server#909 证据

多需求停靠上扫码前取消一条：去掉它的新一版清单（等录入时连同同号录入请求）随取消落定那一次保存暂存、应答之后立即发出，较早各版清单与录入请求同一次保存退役。修之前要等引擎下一轮由 #723 那一段补，而那一轮的轮首补发先把仍期待被取消子批的同版录入请求再发一遍。

## L1 修前红

`red-before-fix.txt`：测试提交 `a42c6e559`（只有测试、没有修复）上跑 `CancelBeforeScanRefreshesWorklistTests`，新加的 4 条（两个用例 × 待送点开关开/关）全红，都红在「取消那一次处理里没有新增清单行」（`Assert.Single() Failure: The collection was empty`），其余 12 条绿。

## 变异取红

`mutations/`：每个变异只改一个文件的一处文本（`run-mutations.sh` 断言替换恰好一处、`git status` 恰好一个文件），`dotnet build --no-incremental` 后跑 `CancelBeforeScanRefreshesWorklistTests`，跑完只还原那一个文件。结果见 `summary.txt`，七个全红：

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| M1-no-stage | 协调器不调 `RemainingStopWorklist.StageAsync`（等于修前） | 新加 4 条 |
| M2-no-entry-fence | 不退役较早各版录入请求 | 新加的等录入用例（同版重发那一句）、#723 的等录入与重连用例 |
| M3-no-entry-request | 等录入时也不暂存录入请求（运行期恒假条件；常量 `false` 那一次被 CS0162 挡在编译，已记在 `summary.txt`） | 新加的等录入用例 ×2 |
| M4-entry-while-loading | 在装时也暂存录入请求 | 新加与 #723 的在装用例 |
| M5-no-send | 保存之后不发（只暂存） | 新加 4 条（线上没有那两行） |
| M6-deadline-dropped | 新一版不带车上那一版的期限 | 新加等录入用例、#723 的期限用例 |
| M7-no-worklist-fence | 不退役较早各版清单 | 10 条 |

## L2

场景 `multi-demand-stop-cancel-one-refreshes-at-once`（合成）。判据不靠计时：新一版清单在发件箱里的 `CreatedAt` 必须等于乙的业务键抑制 `SuppressedAt`——两者由取消落定那一次处理用同一个时刻暂存、同一次保存落库。

- `l2/green/`：修复后 4/4 PASS，跑在 `a5e50ec2c`（CI 的整数组自检 `Test-L2WholeArrayReturn.ps1` 报出场景里两处 `@( )` 包住整数组返回，已改为先赋值再用；本机自检退出码 0）。
- `l2/red-M1-no-stage/`：同一提交上去掉取消时的暂存（`mutations/L2-M1-no-stage.diff`），L2-MDR-02 红：新一版不是取消那一次保存写的，`CreatedAt` 与乙的抑制时刻对不上。这一次 L2-MDR-03 也红，因为读库时引擎下一轮还没写出新版录入请求；改脚本之前那一次对照里它是绿的（引擎已经补完）。所以修前 MDR-03 红不红取决于时机，不拿它当判别；判别靠 MDR-02，同版重发由 L1 守（M2）。

两次都是本机完整运行（含构建）；日志与快照目录已删，只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`。

## 审查返工（M1、L3、L2）

审查 M1：甲的录入已到、引擎在锁外读车位置时，乙的扫码前取消落定、取消那一侧暂存了只期待甲的 R+1 录入请求；引擎随后给甲下装货命令，只按锁外游标结算已退役的 R，R+1 成为未确认、未退役、又在补发白名单里的孤儿。修法：写事务内用 `JourneyStopCursor.LoadAsync` 重读游标，结算本停靠从首版到此刻那一版里未确认、未退役的录入请求。

- `review-m1/red-before-fix.txt`：修 M1 之前（`2272ee80c` 加上新用例）跑本类。`ACancelSettledWhileTheEngineReadsTheVehicleForAnotherEntryLeavesNoEntryRequestToReplay` 两格红：`Entry requests still pending replay while the first demand loads`。同一次运行里 L3 用例 `TwoCancelsBeforeScanInARow…` 两格也红，那是测试自己的断言写错了（数组放进元组按引用比，`Expected (3, ["SUBLOT-001"])` 与 `Actual` 相同），已改为分开断言，与 M1 无关。
- 修后本类 20/20。
- `review-m1/summary-m1.txt`：三个变异全红，只红在 M1 那条用例：R1 改回按锁外游标结算，R2 漏结算此刻那一版，R3 此刻的号改读锁外游标。脚本 `run-m1.sh`，做法同上（先提交、`--no-incremental`、只还原被变异文件）。

L3：`TwoCancelsBeforeScanInARowEachSendAHigherVersionAfterTheirAnswer`（开关开/关）。一站三条，中间不跑引擎、连续取消乙与丙：两版号严格递增、较早各版退役、最后一版只列甲、录入请求只期待甲；按真实连接做法（`DeferOutboundUntilResponseWritten`），应答写出之前一行不发，之后先清单、后录入请求。

L2：`OnboardRecoveryCoordinator` 的 `cancelledAt` 处与场景头注释写明 L2-MDR-02 依赖「终结与新一版清单共用这一个时刻」。
- 返工后本机相关类定向（同前，加 Load 相关类）2152/2152 通过，改动文件 `dotnet format --verify-no-changes` 通过。
