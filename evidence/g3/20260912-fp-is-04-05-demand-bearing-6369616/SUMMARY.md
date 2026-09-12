# `G3`（需求线路）：`FP-IS-04`／`FP-IS-05` **`PASS`**（服务端 `6369616`，协议 `16e2567`）

`DEMAND_BEARING_G3_VECTORS_PASS`，20 条断言全绿，`failedAssertions` 为空。两片 `assuranceLevel` 为 `DEMAND_BEARING_RESTORE`，
`formalSlicePass: true`。本轮没有带 `-Slice`，两片各写一份 `gate-result.json`。

**它取代 `../20260909-graded-per-slice/demand-bearing/`，作为这两片 G3 的现行证据。**那一份绑协议 `f6ee75d`，原样保留、一字未改。

## 为什么要重跑

产品负责人 2026-09-12 批准把单人签名规则移植到协议候选、重跑全部门禁，协议候选因此换成了 `16e2567`（manifest `25fd6689…`）。
这是票 17 那三个 G3 runner 里的第三个；另外两个同日已经重跑，见 `../20260912-fp-is-14-15-staged-harness-58f1a49/` 与
`../20260912-fp-is-14-15-restart-harness-58f1a49/`。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `63696161d036a4907a39f8597fd64cc6e4c755fd`（从 `run-staged-g3.ps1` 的 param 块读回） |
| `onboardHmi` | `f9efa301734128e850cb5460c20265232611ffff` |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `16e2567a7033883f00fc999f7fa08f954dd13a26` |
| `runner` | `c50013f5e376cf823258f9b0825daf7d2f15659e`，`runnerWorktreeCleanAtStart: true` |

`c50013f` 相对修复提交 `58f1a49` 只多了证据提交，`scripts/`、`src/`、`tests/` 没有变化。

## 现场库

`-FieldRunRoot` 与票 17 相同：`C:\Users\szy\w2g-stage\run\fullloop-20260829T131549Z`，即 2026-08-29 那次授权现场全链路运行写下的库。
runner 把它复制到 stage 目录再恢复使用。**跑完之后，现场目录里的每个文件字节数和 SHA-256 都与跑之前相同**，0 个变化。

`fieldStoreProvenance` 照旧如实记录：恢复的库里记着当年说的是 `protocol-v0.1.1`，`protocolCommit` 为 `1531489e…`，
与本次绑定的 `16e2567` 不一致。按用户 2026-09-09 的裁定，这一项是**已知豁免**：只记录，不作断言。豁免只到这一个事实为止。
同一条断言里的其余六项仍然是断言，本轮全部通过：运行中服务端报出的 `protocolCommit`、`protocolTag`，被测构建的
`serverBuildCommit`，以及三个非空守卫。

## 未在本轮证明的

- 这个 runner 不起车载端进程，也不动车；它验证的是恢复现场库之后，服务端对结果与 RIoT 未知这两类向量的处理。
- `FP-IS-01`／`02`／`03`／`07` 在任何 G3 runner 里都没有断言，按 2026-09-09 的裁定不出证据。
- `protocol-v1.0.0` 这个 tag **尚未打**。
