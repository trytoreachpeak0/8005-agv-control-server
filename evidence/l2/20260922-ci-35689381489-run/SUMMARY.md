### Real-onboard L2 (default, 12 runs)

control-server f1286c29aa0cbeffdd750f3016a52590dd4659b9, onboard ecdb3a0be1d95ef51e7d40493808ba4659274f41, simulator fb5f7c593742bf98bc3957b8729a38aad5321f28

Whole-machine committed memory during the step: min 4.83 GiB, peak 8.79 GiB (373 samples, commit-samples.csv in the evidence).

| Run | Result | Seconds | Committed at start (GiB) |
| --- | --- | --- | --- |
| real-onboard-normal-load-01 | PASS | 83 | 6.59 |
| real-onboard-clock-skew-01 | PASS | 32 | 5.18 |
| real-onboard-load-door-closed-empty-reopens-01 | PASS | 82 | 6.35 |
| real-onboard-station-timeout-door-open-01 | PASS | 104 | 6.68 |
| real-onboard-unload-not-emptied-01 | PASS | 76 | 6.56 |
| real-onboard-compensate-then-reconnect-01 | PASS | 51 | 5.6 |
| real-onboard-restart-while-waiting-operator-01 | PASS | 74 | 5.95 |
| real-onboard-cancellation-authorization-lost-01 | PASS | 51 | 5.92 |
| real-onboard-durable-ack-lost-01 | PASS | 77 | 6.13 |
| real-onboard-expected-action-overdue-01 | PASS | 93 | 6.05 |
| real-onboard-restart-with-open-recovery-session-01 | PASS | 46 | 5.49 |
| real-onboard-restart-after-recovery-session-opened-01 | PASS | 46 | 4.83 |
