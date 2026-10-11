**每晚 G3 · 2026-10-11 · 红：staged**

手动触发 · [run](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/38079528832) · ref `2f8af5dbb6498227be23734c932128e8a3821b87` · control-server `2f8af5db` · onboard-hmi `c60013ae`（顶端） · slots-simulator `fb5f7c59`（写死的绑定） · protocol `3f091cb2`（写死的绑定）

cs#582 红证据：临时分支故意把 staged 判据 secondForcedRecoveryWhileFirstUnsettledIsRejected 的期待原因码改错，这一轮必红，不是回归

| runner | 结论 | 红在哪条 |
| --- | --- | --- |
| staged | FAIL | `secondForcedRecoveryWhileFirstUnsettledIsRejected`；status STAGED_SLICE_FAIL, exit 1 |
| restart | PASS |  |
| demand-bearing | PASS |  |
| journey | PASS |  |

自检覆盖运行（`SELF_CHECK_OVERRIDE`，`formalSlicePass=false`），不是门禁证据。红了由当班调度第二天看。
