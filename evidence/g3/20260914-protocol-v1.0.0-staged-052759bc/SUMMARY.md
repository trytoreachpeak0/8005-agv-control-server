# `G3`（主 runner）：`052759bc` 上 `FP-IS-00`／`06`／`14`／`15` **全部 `PASS`**

`STAGED_G3_RECOVERY_REPLAY_PASS`，`assuranceLevel` 为 `STAGED_REBUILD`，30 条断言全绿，四片均为 `formalSlicePass: true`。本轮没有带 `-Slice`。

**它取代 `../20260912-protocol-v1.0.0-staged-harness-a1243a8/`，作为主 runner 这四片的现行证据。**那一份绑的是服务端 `6b21662`，原样保留。

## 为什么要重跑

批次 2 收尾给 `FP-IS-01/02/03/07` 补 G3 面，服务端与车载端都改了产品代码，G3 共享绑定随之挪到服务端 `052759bc`、车载端 `b960108`。
四个 runner 读同一份绑定，所以都在新身份上重跑，理由与变更清单见同日 `../20260914-protocol-v1.0.0-journey-052759bc/SUMMARY.md`。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `052759bca58a04316dfda260249b3b5697f2cf8e` |
| `onboardEvidenceBinding` | `b96010825d43aeee3b861cb3b4716f4d0873c8a0`（`origin/w2g/b3-on-v2` 的顶） |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| `harness` | `6532ec139b171ddd8fadb0723e04bb68248e6114`，`harnessWorktreeCleanAtStart: true` |

`run-result.json` 的 `protocol` 节：`tag` 为 `protocol-v1.0.0`，`tagExists: true`，`approvalStatus` 为 **`APPROVED_RELEASE`**，`manifestSha256` 为 `a0e1deed…`，`g1` 为 `PASS`。

## 结论

| 片 | 状态 | 断言 |
| --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 12 |
| `FP-IS-06` | **`PASS`** | 10 |
| `FP-IS-14` | **`PASS`** | 8 |
| `FP-IS-15` | **`PASS`** | 9 |

断言总数 30，与 `a1243a8` 那一轮相同。

## 未在本轮证明的

- 整体 `fullG3` 与 `releaseCandidate` 仍是 `INCONCLUSIVE`，不含 RC。
- `slicesWithoutSurfaceThisBatch` 为空：`FP-IS-01`／`02`／`03`／`07` 的 G3 面在 journey runner。
