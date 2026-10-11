# cs#544 证据：需求承载 G3 核对迁移回填的占有记录行数

票：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/544
PR：https://github.com/trytoreachpeak0/8005-agv-control-server/pull/606

离线自检 `scripts/Test-DemandBearingClaimRecordBackfill.ps1` 用 `dotnet ef database update <迁移> --connection "Data Source=<一次性库>"` 在 `F:/8005-scratch/cs544-selfcheck` 下建一次性库，跑完即删。**这些库是手工造的，不是外场库，没有进任何门禁证据**，也从未交给 runner 的 `-FieldRunRoot`。

## red/

| 文件 | 提交 | 说明 |
| --- | --- | --- |
| `1-selfcheck-section1-test-commit-8a893487.log` | `8a893487` | 第 1 节：`Get-ExpectedClaimRecordCount` 还是抛错的桩，6 项红。真实迁移留下 55 行与 16 行，等于手算总数 |
| `2-selfcheck-section2-before-narrowing-51e77723.log` | `51e77723` | 第 2 节：收窄之前，只计数的 `UnloadBatches`、`StopClosures`、`TransportDemandCompletions` 缺表时 `counted ... = 0 and went on`；同一次运行第 1 节全绿 |
| `3-selfcheck-section3-test-commit-dc15b3ca.log` | `dc15b3ca` | 第 3 节：迁移少回填 14 行时旧 `noExternalSideEffectsPass` 仍为 `true` |
| `4-mutation-no-lease-backfill-d15a9081.log` | `d15a9081` + 本地变异 | 删掉迁移里 `lease:` 回填那段 `INSERT`（未提交，跑完 `git checkout` 还原）：`expected 55, the migration left 41`、`expected 16, the migration left 9`；旧判据（afterProbe = final）同时为 PASS |
| `5-selfcheck-own-case-bug-44c50b28.log` | `44c50b28` | 自检用例本身的错：拿已迁移库删记录表当「允许缺表」，那样的库既无记录也无租约，runner 拒绝并点名 `VehicleDispatchLeases` 是对的。`d15a9081` 改了用例 |

另有一次在 `4487a00d`（后修订为 `8a893487`）上的运行没有进来：自检脚本自己的语法错（`$Vehicle:` 被当成带驱动器名的变量），还没执行到任何检查。

## green/

| 文件 | 提交 | 说明 |
| --- | --- | --- |
| `1-selfcheck-section1-51e77723.log` | `51e77723` | 第 1 节三种起始状态全绿（第 3 种在这次运行里还有一个拷库只拷主文件的自检错，同一提交里已改为连 `-wal`／`-shm` 一起拷） |
| `2-selfcheck-full-d15a9081.log` | `d15a9081` | 完整一轮：32 项 PASS，exit 0 |
| `3-Test-G3EvidenceHonesty-44c50b28.log`、`3-Test-G3RunnerClaims-44c50b28.log` | `44c50b28` | 既有自检不受影响 |
| `4-demand-bearing-g3-default-path-d15a9081/` | `d15a9081` | 默认路径需求承载 G3 完整一轮（`-SelfCheckControlServerCommit`，`SYNTHETIC_RIG`，`runnerSource: COMMITTED_RUNNER`）：`DEMAND_BEARING_G3_VECTORS_PASS`，16 项断言全 PASS。`claimRecordBackfill`：`ALREADY_RETIRED`，原有 1、应有 1、afterProbe 实际 1。三个切片 `gate-result.json` 都带该字段；`formalSlicePass: false`、`formalSliceWithheldReason: SELF_CHECK_OVERRIDE`，不是门禁证据 |
