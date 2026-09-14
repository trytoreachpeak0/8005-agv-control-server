# `G3`（需求线路 runner）：`052759bc` 上 `FP-IS-04`／`05` **全部 `PASS`**

`DEMAND_BEARING_G3_VECTORS_PASS`，`assuranceLevel` 为 `DEMAND_BEARING_RESTORE`，20 条断言全绿，两片均为 `formalSlicePass: true`。本轮没有带 `-Slice`。

**它取代 `../20260912-protocol-v1.0.0-demand-bearing-harness-a1243a8/`，作为这两片的现行证据。**那一份绑的是服务端 `6b21662`，原样保留。

## 为什么要重跑

与同日主 runner 相同：G3 共享绑定挪到服务端 `052759bc`、车载端 `b960108`，四个 runner 都在新身份上重跑。
变更清单见 `../20260914-protocol-v1.0.0-journey-052759bc/SUMMARY.md`。`-FieldRunRoot` 仍是 2026-08-29 那次授权现场运行 `fullloop-20260829T131549Z`。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `052759bca58a04316dfda260249b3b5697f2cf8e` |
| `onboardHmi` | `b96010825d43aeee3b861cb3b4716f4d0873c8a0` |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793`（`protocol-v1.0.0`） |
| `runner` | `6532ec139b171ddd8fadb0723e04bb68248e6114`，`runnerWorktreeCleanAtStart: true` |

## 结论

| 片 | 状态 | 断言 |
| --- | --- | --- |
| `FP-IS-04` | **`PASS`** | 15 |
| `FP-IS-05` | **`PASS`** | 9 |

## 那条已知豁免，照旧如实记录

`fieldStoreProvenance`：被恢复的现场库记的 `protocolCommit` 是 `1531489e…`（2026-08-29 现场运行说的 `protocol-v0.1.1`），
`matchesBoundProtocolCommit` 照实为 **`false`**，`exemption` 为 `TICKET_17_KNOWN_EXEMPTION_FIELD_STORE_HISTORY`（用户 2026-09-09 裁定，范围只限这一项事实）。
**这两片的通过仍以这条豁免为前提**；真正的解是重采一次 v2 现场运行，未做。

## 未在本轮证明的

- 整体 `fullG3` 与 `releaseCandidate` 仍是 `INCONCLUSIVE`，不含 RC。
