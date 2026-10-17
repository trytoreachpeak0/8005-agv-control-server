# cs#730 受理判据改读 AcceptedAt：本机合成 L2 与离线核对

被测提交：control-server `79aa158d`（判据改动），本机合成 L2，2026-10-17 22:28～22:44（CST），本机时段由调度 C11 放行（看板第 227 行持有者 cs#730）。
每个子目录只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，日志与库快照没有入库（原件在 `F:/8005-scratch/cs730-l2/`）。

## 七轮合成 L2

| 目录 | 做了什么 | 结果 |
| --- | --- | --- |
| `r1-normal-load-red` | 临时注入：先等到积压行被后续派车轮改写成 `DEMAND_ALREADY_ACCEPTED`，再同时判旧判据（`L2-NL-02-OLD`）与新判据 | 受理后约 1.2 秒被改写；旧判据 FAIL，新 `L2-NL-02` PASS，其余 8 条 PASS。注入没有提交 |
| `r2-normal-load-green` | 不注入 | 9/9 PASS |
| `r3-normal-load-m1` | 产品变异 M1：删掉 `WireToGateStore` 两处 `backlog.AcceptedAt = snapshot.AcceptedAt;` | 只有 `L2-NL-02` FAIL（`AcceptedAt=(null)`）。这一轮原因码读到的是 `ACCEPTED`，旧判据会放过 |
| `r4-normal-load-m2` | 产品变异 M2：同两处写成 `snapshot.AcceptedAt.AddSeconds(1)` | 只有 `L2-NL-02` FAIL（与 `AcceptedDemands.AcceptedAt` 不等） |
| `g-route-graph-engine` | 取绿 | 7/7 PASS |
| `g-area-eqp-unique-across-task-types` | 取绿 | 2/2 PASS |
| `g-session-established-while-moving` | 取绿 | 10/10 PASS |

每次变异前都核对过只改了一个文件、恰好 2 行；跑完 `git checkout` 还原，并核对工作树干净。

## 离线核对

- `test-l2-backlog-acceptance.txt`：`scripts/l2/Test-L2BacklogAcceptance.ps1` 的 12 条（3 绿 9 红），全部符合预期。
- `offline-snapshot-old-vs-new.txt`：拿 CI 证据的场景结束快照（`db-JourneyBacklog.json`、`db-AcceptedDemands.json`）逐条已受理需求套旧、新判据。
  - l2 run 38721626350（cs#724）的 `normal-load-01`：旧 FAIL、新 PASS。这就是 cs#730 报的那次误红，CI 当时读到的值也是 `DEMAND_ALREADY_ACCEPTED`。
  - 同一 run 的 `session-established-while-moving-01..03`：场景结束快照里原因码都已被改写；旧判据当时读得早，所以 CI 是绿的，这说明改写每次都会发生，差别只在读的时刻。新判据三遍都绿。
  - `real-onboard-clock-skew`（CI 37013472279、37953803085）：新旧判据都绿。**真装置未实跑。**
