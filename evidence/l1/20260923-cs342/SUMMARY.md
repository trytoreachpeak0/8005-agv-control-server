# control-server#342 断线重连基于模型随机测试：量测与判别力证据

全部是本机（控制端笔记本）跑出来的 L1 输出，没有真装置。完整的解读在 PR 正文里，这里只说明每个文件是什么。

## 模型的两个版本

- **原型**（`prototype/`，提交 `75cfd407`）：握手直接写库，8 种动作，只判「录入请求到车上或看板有码、不每轮抛同一异常」。
- **最终**（`final/`；`measure-*` 用提交 `0eb11aed` 的测试文件，`regression-*` 用 `5e3d277a` 的，两者的模型文件相同，`5e3d277a` 只把挂起单那条回归用例换成了模型删减出的序列）：握手经真实 `OnboardMessageProcessor`，加了安全变化（三种送达方式）、RIoT 单挂起与继续、同 messageId 不同内容的重放；不变量四条全开。动作多了，同样的种子生成的序列与原型不同，两版的种子不能互相对照。

## 在哪个提交上跑

| 目录里的名字 | 提交 | 是什么 |
| --- | --- | --- |
| `af01fd27` | `af01fd27` | cs#331 修复前 |
| `62d5c560` | `62d5c560` | cs#331 第一版修复（PR #338 的第一个实现提交） |
| `18172346` | `18172346` | cs#331 合入（PR #338 合入提交），cs#340 修复前 |
| `0eb11aed`、`5e3d277a` | 本分支，含 `384b9b69` | cs#340 合入（PR #343 合入提交）之后 |
| `cb44fb60` | PR #343 头 | 只用来做变异：在它上面把重放护栏改成放过语义不同的消息 |

测试文件（两个模型文件、回归用例、`Directory.Packages.props`、测试 csproj、`packages.lock.json`）拷进各提交的分离 worktree 再构建；`repos/` 克隆没有动。

## 文件

- `*/measure-<提交>.txt`：`ReconnectModelTests.PrototypeMeasurement` 的报告。固定种子 300 个组合，每个组合的耗时，每一类违规的次数、种子与序列，第一个违规组合的逐步记录，CsCheck 化简与确定性删减的结果。
- `prototype/calibration-*.txt`：四串手写序列（后来成了回归用例）在三个提交上的判定。`calibration-62d5c560-deadline-off-probe.txt` 是把模型的离站等待期限临时关掉之后在 `62d5c560` 上的结果，跑完已还原。
- `prototype/replay-af01fd27-stuck-seeds-on-18172346.txt`：`af01fd27` 上卡在取货站的 17 个种子，在 `18172346` 上逐个复跑。
- `final/regression-<提交>.log`：`ReconnectModelRegressionTests` 在各提交上的输出，修前红、修后绿。
- `final/regression-unskipped-hmi206-on-5e3d277a.log`：把 onboard-hmi#206 那条的 Skip 临时去掉，在本分支上跑出的红，跑完已还原。
- `final/*mutant-replay-guard-on-cb44fb60*`：变异护栏之后，模型找到「同 messageId 不同内容被回了确认」，对应的回归用例变红。变异跑完已还原。
