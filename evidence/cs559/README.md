# cs#559 证据：服务端 G2 相对路径 `-Output`

票：trytoreachpeak0/8005-agv-control-server#559。全部在本机 2026-10-11 跑出，`-ProtocolManifest` 用仓内
`vendor/8005-agv-protocol/manifest/release.json`（与协议发布清单逐字节相同，`ProtocolIdentityArchitectureTests` 钉住其哈希）。

## 红：修复前（`red-before-fix/`）

提交 `da6e40e2`（`fp/v2-impl` 顶端，未改脚本），在 worktree 根目录执行：

```
pwsh -NoProfile -File scripts/test-wire-to-gate.ps1 -Slice FP-IS-00 -ProtocolManifest vendor/8005-agv-protocol/manifest/release.json -Output evidence/cs559/red-before-fix/fp-is-00
```

- `fp-is-00/gate-result.json`：`status: PASS`、`schemaConformance: null`、`testExitCode: 0`。
- `report-found-under-bin/`：跑完后在 `tests/ControlServer.Tests/bin/Release/net8.0/win-x64/evidence/cs559/red-before-fix/fp-is-00/`
  找到的报告（路径见 `WHERE.txt`），`linesChecked 5116`、违约 0——校验做了，报告落进了构建输出。
- 当时工作区里已有本票未提交的新测试文件；它不带 `IntegrationSlice` 标记，不在这一片的过滤范围内，不影响结果。
- TRX（340 KB）未入库。

## 绿：修复后（`green-after-fix/`）

提交 `391429dc`，同一条命令，只换输出目录：`status: PASS`，`schemaConformance.linesChecked 5116`、`linesInViolation 0`，
`schemaConformanceMissing: false`，`schema-coverage.json` 在 `-Output` 下，bin 下没有 `evidence/`。

## 报告缺失时不判 PASS（`missing-report-injection/`）

修复后的脚本，`WIRE_TO_GATE_DOTNET_EXE` 指向一个退出码 0、但不写报告的替身（`fake-dotnet.ps1.txt`）：
脚本退出码 1，`status: FAIL`、`schemaConformanceMissing: true`、`testExitCode: 0`，控制台打出 `SCHEMA_REPORT_MISSING`。

## 零报文核实（`zero-message-check/`）

直接调用 `tools/ControlServer.SchemaConformance`，`--lines` 给一个空文件：退出码 0，照样写出 `schema-coverage.json`（`linesChecked 0`）。
服务端因此没有「这一片不发报文所以没有报告」的情形，与车载端 `run-w2g-g2.ps1` 不同；「测试退出码 0 + 无报告」只可能是报告丢了。

## TDD 红（`tdd/`）

- `01-red-c7978d955.txt`：只提交了第一条测试时 `ARelativeOutputGetsTheSchemaReportNextToTheGateResult` 失败（`schema-coverage.json is not under -Output.`）。
- `02-red-e609663c5.txt`：只提交了第二条测试时 `AMissingReportIsNotCertifiedAsPass` 失败（`Expected: Not "PASS"`）。

## 变异（`mutation/`）

另外三条用例（失败退出码保留、重复 `-Output` 被拒、相对 `-ProtocolManifest`）与 AST 顺序护栏写出时即为绿，红证据靠变异：

| 变异 | 变红的用例 |
| --- | --- |
| M1 删掉两行路径转换 | AST 护栏，以及三条行为用例（报告丢失，脚本按第 2 条退出 1） |
| M2 把 `$Output` 的转换挪到 `New-Item` 之后 | AST 护栏 |
| M3 退出码改成「报告缺失就退出 1」（掩盖测试自身的失败码） | `AFailedRunKeepsItsExitCodeAndIsNotReportedAsALostReport` |

每个文件开头是变异的 diff，跑完已 `git checkout` 还原。
