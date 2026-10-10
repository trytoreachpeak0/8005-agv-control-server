# control-server#339 红证据

三份都是在「测试先提交、实现还没提交」的那个提交上跑的，输出按失败块裁剪过（只留失败名、错误信息与汇总行）。

| 文件 | 提交 | 跑了什么 | 结果 |
| --- | --- | --- | --- |
| `probes-at-756845fb.txt` | `756845fb` | `RefilledStationDeadlineReachesVehicleTests` 第一步五条探针 | 5 条全红 |
| `cs331-rewrite-at-238c8677.txt` | `238c8677` | cs#331 改写后的两条（`ACutOnTheWorklistThenARefillSendsANewerWorklistInsteadOfFailingARound`、`AnAcknowledgedWorklistThatDiffersBeyondItsDeadlineIsStillRefusedAndTheBoardSaysSo`） | 前一条红（重跑那一轮抛 `ProtocolContentConflictException`），后一条绿（它是护栏，判别力见反向验证 R7） |
| `holding-probe-at-b4840642.txt` | `b4840642` | 持货探针加强断言之后 | 持货那一条红 |

第一步探针在 `756845fb` 上的实际后果：

- 等录入、装货中结果未回、到站重跑、真车载端未就绪形状四条：车上仍是断线前的期限 `01:00:30`，服务端已重填为 `01:00:37`。
- 持货旅程：每轮 `ProtocolContentConflictException`，看板 `JOURNEY_ADVANCE_FAILED`，阶段停在 `AwaitingPickupArrival`；车按同号不同内容拒收装货阶段快照（修订号 1），每轮一次。
