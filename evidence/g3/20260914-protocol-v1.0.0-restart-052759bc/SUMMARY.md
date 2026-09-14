# `G3`（进程重启 runner）：`052759bc` 上 `FP-IS-00`／`06`／`14`／`15` **全部 `PASS`**

`STAGED_G3_PROCESS_RESTART_PASS`，`assuranceLevel` 为 `STAGED_REBUILD`，25 条断言全绿，四片均为 `formalSlicePass: true`。本轮没有带 `-Slice`。

**它取代 `../20260912-protocol-v1.0.0-restart-harness-a1243a8/`，作为进程重启 runner 这四片的现行证据。**那一份绑的是服务端 `6b21662`，原样保留。

## 为什么要重跑

与同日主 runner 相同：G3 共享绑定挪到服务端 `052759bc`、车载端 `b960108`，四个 runner 都在新身份上重跑。
变更清单见 `../20260914-protocol-v1.0.0-journey-052759bc/SUMMARY.md`。

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
| `FP-IS-00` | **`PASS`** | 17 |
| `FP-IS-06` | **`PASS`** | 12 |
| `FP-IS-14` | **`PASS`** | 12 |
| `FP-IS-15` | **`PASS`** | 11 |

## 未在本轮证明的

- 整体 `fullG3` 与 `releaseCandidate` 仍是 `INCONCLUSIVE`，不含 RC。
