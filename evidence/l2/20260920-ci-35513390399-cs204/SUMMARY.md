### Real-onboard L2 (consecutive, 7 runs)

control-server de29ec1649596012e39bb71de45f22ba4ff10605, onboard 24af41e4ab769f10cd15381fd3b4a56babb62c78, simulator fb5f7c593742bf98bc3957b8729a38aad5321f28

Whole-machine committed memory during the step: min 3.88 GiB, peak 7.71 GiB (299 samples, commit-samples.csv in the evidence).

| Run | Result | Seconds | Committed at start (GiB) |
| --- | --- | --- | --- |
| real-onboard-restart-while-waiting-operator-01 | PASS | 92 | 3.84 |
| real-onboard-durable-ack-lost-01 | PASS | 65 | 3.88 |
| real-onboard-durable-ack-lost-02 | PASS | 64 | 3.87 |
| real-onboard-durable-ack-lost-03 | FAIL (1) | 171 | 3.86 |
| real-onboard-expected-action-overdue-01 | PASS | 94 | 5.77 |
| real-onboard-expected-action-overdue-02 | PASS | 87 | 6.61 |
| real-onboard-expected-action-overdue-03 | PASS | 86 | 5.36 |
