# cs#673 证据

需求腿（开往取货站、开往卸货站）建单发出过、结果未知、RIoT 一直查无此单时，服务端凭 cs#611 的共用判定判「没建成」，交给 cs#318 的同车重建；
凭据凑不齐时由人经故障处置入口 `DECLARE_ORDER_NEVER_APPEARED` 确认，再走 cs#345 的三个出口。旧单事后冒出来，外来单监督器挡车、不取消。

## 文件

| 文件 | 内容 |
| --- | --- |
| `mutations.txt` | 两次本机时段的变异原始输出：`5d8092429` 上 19 项，`069cec452` 上只跑 P8 |

## 变异

每个变异只改一个文件，假条件用运行期写法（`Environment.TickCount64 < 0`），构建一次后 `dotnet test --no-build --filter` 跑对应类，随即
`git checkout` 还原那一个文件并核对 `git status` 为空；改动文件数不是恰好一个就判作废。两次都没有作废轮，最后构建未变异版本 exit 0、工作树干净。

**结果：20 次运行，全部取红。**第一次 19 项中 18 项红、P8 存活；P8 存活的原因见 PR #680 的变异评论（途中追加那一格只靠旅程码挡），补用例
`MultiVehicleExecutionTests.AVehicleWhoseLegWasTakenAsNeverCreatedTakesNoAppendedDemandBeforeTheRebuild` 之后在第二次时段重跑 P8：红 1（exit 1）。

| 变异 | 改了什么 | 结果 |
| --- | --- | --- |
| M01 | 闸门前不调判定 | 红 8 |
| P6 | 只撤闸门后那一处调用 | 红 1 |
| M03 | 重建新单分支不调 | 红 1 |
| M04 | 引擎去掉「意图无单号」前提 | 红 1 |
| M05 | 引擎去掉审计前提（审查 S1） | 红 1 |
| M06 | 新来源不进 `CarriesCargoAsync` | 红 1 |
| P7 | 只从 `ClaimCargoEvidenceRequestAsync` 去掉新来源 | 红 1 |
| P5 | `CountsAsAgain`：没建成之后只有取消才算 | 红 1 |
| P8 | 新码不进 `IsStalledOrderReason` | 第一次存活；补用例后红 1 |
| M10 | 监督器不认 `OWN_DEMAND_LEG_ORDER_ABANDONED` | 红 1 |
| M11 | 人工出口忽略意图单号 | 红 1 |
| M12 | 人工出口忽略精确查无此单 | 红 1 |
| M13 | 人工出口不判太早 | 红 1 |
| M14 | 不结束被取代的重建（审查 H1） | 红 1 |
| M15 | H2 共用判定恒假 | 红 2 |
| M16 | M1 空闲返回排除恒假 | 红 1 |
| V-a | 空闲返回排除去掉 `row.OrderId == null` | 红 1 |
| V-b | `TakenAsNeverCreatedAsync` 去掉 `intent.OrderId is null` | 红 1 |
| M17 | 释放前的精确查询恒放 | 红 2 |

## 修前红

审查探针 P1、P2、P3、S1 与增量审查 S1 的用例，各自把对应的那一个源文件退回修复前的提交再跑，都是 `Failed`；随即还原，工作树干净（PR #680 正文「第一阶段审查的修改」「增量审查的修改」两节）。

## 定向回归

merge `fp/v2-impl`（`5e1d7e07`，含 #679）之后，在 `069cec452` 上：`Passed: 1613, Failed: 0`。全量交 CI，本票只跑这一轮。
