# CONTROL_SERVER_G2 — protocol-v0.2.0 发布后重跑

运行日期：2026-09-08（本地，UTC+08:00）

## 这轮为什么存在

0.2.0 是 breaking release（`protocolVersion` 由 1 升到 2），发布即作废两端
`W2G-IS-00` 到 `W2G-IS-07` 的 G1/G2/G3 证据。同一批切片在**候选期**跑过一轮
（`../20260908-candidate-0.2.0`，八切片全 PASS），但那轮的每份证据都标着
`protocolReleaseStatus: UNRELEASED_CANDIDATE` 与
`protocolTag: (unreleased candidate 0.2.0)`——它证明的是「服务端与一份未签名的候选
契约一致」，不能当作发布后的证据用。这一轮把同样的东西在**已发布身份**下重新测量。

## 契约身份

| 项 | 值 |
| --- | --- |
| protocolTag | `protocol-v0.2.0` |
| protocolReleaseStatus | `RELEASED` |
| protocolReleaseVersion | `0.2.0` |
| protocolRepositoryCommit | `dff1686751d1d05c4c06b19ac024b41e84bb8078` |
| protocolManifestSha256 | `31bb730565f21b8011b88d447b5b81fc4be28ba34b2fdfb3592bd7586f0a59d6` |
| implementationCommit | `d95e7cdc411cad01af85af4260c38821b7e1a562` |

跑门禁时 `8005-agv-control-server` 的工作树是干净的（只余一个与本次无关的未跟踪
L2 证据目录），所以 `implementationCommit` 就是实际被测的代码。

## 结果

八个切片全部 PASS，`testExitCode` 均为 0，不带 `-UnreleasedCandidate`。

| 切片 | 结果 | 耗时 | vector 数 |
| --- | --- | --- | --- |
| W2G-IS-00 | PASS | 46.8 s | 4 |
| W2G-IS-01 | PASS | 32.1 s | 1 |
| W2G-IS-02 | PASS | 12.6 s | 4 |
| W2G-IS-03 | PASS | 13.0 s | 2 |
| W2G-IS-04 | PASS | 22.0 s | 1 |
| W2G-IS-05 | PASS | 9.3 s | 2 |
| W2G-IS-06 | PASS | 37.6 s | 4 |
| W2G-IS-07 | PASS | 9.2 s | 6 |

合计 182.6 秒。每个切片目录下有 `gate-result.json` 与 `control-<切片>.trx`。

## 一处容易被漏掉的差异

**`W2G-IS-02` 这轮是 4 个 vector，候选那轮是 3 个。**0.2.0 给它加了
`CV-LOAD-CANCELLATION-BEFORE-LOAD`（protocol 的 `db064d2`）。

这不是这轮跑法变了，是脚本此前会漏掉它：`test-wire-to-gate.ps1` 的
`-UnreleasedCandidate` 分支会读候选自己的 `integration-slices/index.json` 覆盖切片到
vector 的映射，而 **released 分支用的是脚本里硬编码的那张表**。把哈希 pin 到 0.2.0
的同时不同步那张表，这份证据的 `vectorIds` 就会少一条，而且不会有任何东西报错。
表已随 `d95e7cd` 一起同步，改 pin 时要连它一起核对。
