### Real-onboard L2 (default, 12 runs)

control-server f2ddd40522432925580d6048dca2d6d54171a7a8, onboard 86d42ce5362a8273525b8ba1acb387e4331bcfed, simulator fb5f7c593742bf98bc3957b8729a38aad5321f28

Whole-machine committed memory during the step: min 5.15 GiB, peak 9.17 GiB (366 samples, commit-samples.csv in the evidence).

| Run | Result | Seconds | Committed at start (GiB) |
| --- | --- | --- | --- |
| real-onboard-normal-load-01 | PASS | 79 | 6.83 |
| real-onboard-clock-skew-01 | PASS | 33 | 6.37 |
| real-onboard-load-door-closed-empty-reopens-01 | PASS | 82 | 6.21 |
| real-onboard-station-timeout-door-open-01 | PASS | 101 | 6.22 |
| real-onboard-unload-not-emptied-01 | PASS | 76 | 6.52 |
| real-onboard-compensate-then-reconnect-01 | PASS | 47 | 6.42 |
| real-onboard-restart-while-waiting-operator-01 | PASS | 73 | 6.62 |
| real-onboard-cancellation-authorization-lost-01 | PASS | 51 | 4.86 |
| real-onboard-durable-ack-lost-01 | PASS | 74 | 5.92 |
| real-onboard-expected-action-overdue-01 | PASS | 92 | 5.75 |
| real-onboard-restart-with-open-recovery-session-01 | PASS | 47 | 5.27 |
| real-onboard-restart-after-recovery-session-opened-01 | PASS | 44 | 5.24 |
