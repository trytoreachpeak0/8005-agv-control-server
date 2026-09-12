# `G3`（需求线路）：已发布的 `protocol-v1.0.0` 上 `FP-IS-04`／`FP-IS-05` **`PASS`**

`DEMAND_BEARING_G3_VECTORS_PASS`，20 条断言全绿，`failedAssertions` 为空。两片的 `assuranceLevel` 都是 `DEMAND_BEARING_RESTORE`，`formalSlicePass: true`。

**它取代 `../20260912-fp-is-04-05-demand-bearing-6369616/`，作为这两片 G3 的现行证据。**那一份绑的是协议候选 `16e2567`，原样保留。
重跑原因同 `../20260912-protocol-v1.0.0-staged-harness-a1243a8/SUMMARY.md`。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `6b21662c60a2e13aaf86043b146d3d886cf91dd1` |
| `onboardHmi` | `c86bac5eaec57c36351f7d45b458deaa42fde22e` |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| `runner` | `51a1f1cfbadcd7b322b29636d5b4ab998b27b314`，`runnerWorktreeCleanAtStart: true`（相对 `a1243a8` 只多了证据提交） |

运行中的服务端 `/version` 报告的身份：

- `protocolTag`：`protocol-v1.0.0`
- `protocolCommit`：`9f22db8`
- `manifestSha256`：`a0e1deed…`
- `approvalStatus`：**`APPROVED_RELEASE`**

## 现场库

`-FieldRunRoot` 仍是 `fullloop-20260829T131549Z`，runner 先复制一份再恢复使用。跑之前和跑之后，现场目录里每个文件的字节数与 SHA-256 都相同，没有任何变化。

`fieldStoreProvenance` 如实记录：现场库里的历史 `protocolCommit` 是 `1531489e…`（`protocol-v0.1.1`），与这次绑定的 commit 不一致。按用户 2026-09-09 的裁定，
这是已知豁免：**只记录，不作断言**，豁免的范围只到这一个事实。同一条断言里的其余六项仍然是断言，本轮全部通过。

## 未在本轮证明的

- 这个 runner 不启动车载端进程，也不动车。
- `FP-IS-01`／`02`／`03`／`07` 在任何 G3 runner 里都没有断言，不出 G3 证据。
