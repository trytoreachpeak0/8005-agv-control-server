# cs#544 证据：需求承载 G3 核对迁移回填的占有记录行数

票：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/544
PR：https://github.com/trytoreachpeak0/8005-agv-control-server/pull/606

离线自检 `scripts/Test-DemandBearingClaimRecordBackfill.ps1` 用 `dotnet ef database update <迁移> --connection "Data Source=<一次性库>"` 在 `F:/8005-scratch/cs544-selfcheck` 下建一次性库，跑完即删（自述）。这些库是手工造的，不是外场库，没有进任何门禁证据，也从未交给 runner 的 `-FieldRunRoot`（自述；本目录里没有任何一份 runner 运行用的是 `FIELD_RUN`，见 `green/4-*/run-result.json` 的 `storeSource`）。

下表每行分两栏：「读到的」是日志或 JSON 里原样能看到的；「自述／推论」是做票会话自己的说法或由读到的推出来的，读者无法从本目录核对。

## red/

| 文件 | 提交 | 读到的 | 自述／推论 |
| --- | --- | --- | --- |
| `1-selfcheck-section1-test-commit-8a893487.log` | `8a893487` | 6 项 FAIL，报错 `Get-ExpectedClaimRecordCount is not implemented yet`；`the migration left 55`、`left 16` | 55 与 16 等于手算总数，说明造的数据与手算表本身对（推论） |
| `2-selfcheck-section2-before-narrowing-51e77723.log` | `51e77723` | `UnloadBatches`、`StopClosures`、`TransportDemandCompletions` 缺表时 `counted ... = 0 and went on`；另 6 张表报的是 SQLite `no such table`；同一次第 1 节全 PASS | 后者是被后面的 SELECT 偶然撞上，不是 runner 自己的拒绝（推论） |
| `3-selfcheck-section3-test-commit-dc15b3ca.log` | `dc15b3ca` | 应有 55、afterProbe 41、final 41 时 `{"backfill":null,"noExternal":true,...}` | 这是旧 runner 判据在回填不足时仍为绿的直接证据 |
| `4-mutation-no-lease-backfill-d15a9081.log` | `d15a9081` + 本地变异 | `expected 55, the migration left 41`、`expected 16, the migration left 9`，exit 码在会话里为 1 | 变异是删掉迁移里 `lease:` 那段 `INSERT`，未提交、跑完 `git checkout` 还原（自述）；少的 14 与 7 行等于手算的 `lease:` 回填数（推论） |
| `5-selfcheck-own-case-bug-44c50b28.log` | `44c50b28` | 1 项 FAIL：`read without VehiclePurposeClaimRecords -- it threw: CONTROL_TABLE_MISSING: VehicleDispatchLeases` | 用例错：已迁移的库没有租约表，再删记录表就是不可能存在的库，runner 拒绝是对的（推论）；`d15a9081` 改了用例，合法的「无记录表」由第 1b 节覆盖 |
| `6-skipstores-mutation-no-step2-writeback.log` | 审查后 | 第 0 节两项 FAIL：`leaseBackfills=18 (hand-computed 14); expected=59`、`leaseBackfills=10 (hand-computed 7); expected=19` | 变异是 runner 副本里删掉第 2 步写回 `recordKeys` 那一行（自述）；多出的 4 与 3 正是 F、Q 两组（推论） |
| `7-skipstores-mutation-matches-true.log` | 审查后 | `FAIL the evidence record's matches is the judgement itself -- ... matches = $true` | 变异是 runner 副本里把 `matches` 改成 `$true`（自述） |

另有一次在 `4487a00d`（后修订为 `8a893487`）上的运行没有进来：自检脚本自己的语法错，还没执行到任何检查（自述）。

## green/

| 文件 | 提交 | 读到的 | 自述／推论 |
| --- | --- | --- | --- |
| `1-selfcheck-section1-51e77723.log` | `51e77723` | 第 1 节 1a、1b PASS；1c 一项 FAIL | 1c 的 FAIL 是拷库只拷主文件、漏了 `-wal` 的自检错，同一提交已改（自述） |
| `2-selfcheck-full-d15a9081.log` | `d15a9081` | 32 项 PASS，末行 `every check as expected.` | 审查后的改动未再完整跑一轮：1a、1b 造库 SQL 与此版逐字相同（117 条，会话内比对，自述） |
| `3-Test-G3EvidenceHonesty-44c50b28.log`、`3-Test-G3RunnerClaims-44c50b28.log` | `44c50b28` | `every check as expected.`、`G3_CLAIMS_OK` | merge 后与审查后复跑同样通过（自述） |
| `4-demand-bearing-g3-default-path-d15a9081/` | `d15a9081` | `run-result.json`：`status: DEMAND_BEARING_G3_VECTORS_PASS`，16 项断言 PASS，`controlServerCommitSource: SELF_CHECK_OVERRIDE`，`runnerSource: COMMITTED_RUNNER`，`claimRecordBackfill` 应有 1、实际 1；三个 `slices/*/gate-result.json` 带 `claimRecordBackfill`，`formalSlicePass: false` | 不是门禁证据，绑定未动 |
| `5-skipstores-after-review.log` | 审查后 | `-SkipStores` 全 PASS（第 0 节两项、第 3 节判据与两条 AST 检查） | — |

`green/4-*/store-generator/` 精简过：只留 `assertions.json`（`run-result.json` 的 `storeProvenance.generator` 取自它）与 `SUMMARY.md`；`logs/`、`snapshots/`、`timeline.jsonl` 已删，所以 `run-result.json` 的 `evidenceFiles` 里列着、但本目录里找不到这些文件。删除发生在它们入库（`d00683f3`）之后，分支历史里仍有。
