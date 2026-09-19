### Real-onboard L2 (default, 11 runs)

control-server 84a749e335a3c38cf0d317cf44cccf5762b434ca, onboard 4d716340982de4e39339c2151c291efe1a21e1d1, simulator fb5f7c593742bf98bc3957b8729a38aad5321f28

Whole-machine committed memory during the step: min 3.46 GiB, peak 11.66 GiB (386 samples, commit-samples.csv in the evidence).

| Run | Result | Seconds | Committed at start (GiB) |
| --- | --- | --- | --- |
| real-onboard-normal-load-01 | PASS | 83 | 9.49 |
| real-onboard-clock-skew-01 | PASS | 40 | 9.12 |
| real-onboard-restart-with-open-recovery-session-01 | FAIL (1) | 122 | 8.89 |
| real-onboard-load-door-closed-empty-reopens-01 | PASS | 96 | 6.94 |
| real-onboard-station-timeout-door-open-01 | PASS | 97 | 5.66 |
| real-onboard-unload-not-emptied-01 | PASS | 67 | 3.51 |
| real-onboard-compensate-then-reconnect-01 | PASS | 41 | 3.5 |
| real-onboard-restart-while-waiting-operator-01 | PASS | 61 | 3.45 |
| real-onboard-cancellation-authorization-lost-01 | PASS | 44 | 3.46 |
| real-onboard-durable-ack-lost-01 | PASS | 69 | 3.45 |
| real-onboard-expected-action-overdue-01 | PASS | 85 | 3.44 |
