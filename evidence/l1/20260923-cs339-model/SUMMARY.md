# control-server#339 断线重连模型测试（cs#342）

| 文件 | 内容 |
| --- | --- |
| `reconnect-model-classes-at-6013c0da.txt` | `ReconnectModelTests`、`ReconnectModelRegressionTests` 两个类在 `6013c0da` 上的结果（`FullyQualifiedName~ReconnectModel`）：全部通过，跳过的是三条只在显式要求时跑的与 onboard-hmi#206 那条 Skip；固定种子那条打印 `known defect onboard-hmi#206: 96 violation(s) in 200 sequences` |
| `prototype-1000-at-6013c0da.txt` | 本分支 `PrototypeMeasurement`，1000 个组合（`CS342_ITER=1000`，主种子 342） |
| `prototype-1000-at-fc3dda3c.txt` | 同样 1000 个组合，在集成分支基点 `fc3dda3c` 上 |

两份报告只留汇总段与首例违规、化简结果；逐步记录未入库。

对照（读到的）：

| 项 | `fc3dda3c` | 本分支 |
| --- | --- | --- |
| 录入请求到车 | 1000 / 1000 | 1000 / 1000 |
| 最后一次恢复到录入请求到车的轮数 | 1 轮 983，2 轮 17 | 1 轮 1000 |
| 车的同号拒收 / 号回退 | 0 / 0 | 0 / 0 |
| 语义不同的消息被放过 | 0 | 0 |
| `LegitimateMessageRefused` | 399 | 399，首例与化简结果相同（onboard-hmi#206 的形状） |

「2 轮」那 17 个是 cs#331 定的「断在清单那一张、第一轮失败一次」，本票让它不再失败，所以全部变成 1 轮。
「带等人码失败的轮数」基线多出的 2 个 `ONBOARD_SESSION_LOST`（豁免）落在那些失败轮上，它们消失与上一条是同一件事。这一行是机会数，不是违规。

没有新类别。399 条违规报告只打印首例，全部说明没有逐条比对；依据是与基线同为 399、首例与化简结果相同，
且固定种子那条用例按已知缺陷表判定通过（推的）。模型测不到持货等单，也没有「车上期限与服务端一致」这条不变量，
所以它只说明本票没有把别的东西改坏，修复本身的证据在 `RefilledStationDeadlineReachesVehicleTests`。
