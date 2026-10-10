### Real-onboard L2 (consecutive, 31 runs)

control-server 563242d071de6f243c59f11722b197d683fee2a4, onboard b9e67a538ba4cdf1916d201a08af40dd28270d14, simulator fb5f7c593742bf98bc3957b8729a38aad5321f28

Whole-machine committed memory during the step: min 4.34 GiB, peak 11.63 GiB (1558 samples, commit-samples.csv in the evidence).

| Run | Result | Seconds | Committed at start (GiB) |
| --- | --- | --- | --- |
| real-onboard-normal-load-01 | PASS | 186 | 8.72 |
| real-onboard-clock-skew-01 | PASS | 53 | 8.62 |
| real-onboard-load-door-closed-empty-reopens-01 | PASS | 120 | 8.26 |
| real-onboard-station-timeout-door-open-01 | PASS | 121 | 8.13 |
| real-onboard-unload-not-emptied-01 | PASS | 111 | 8.23 |
| real-onboard-compensate-then-reconnect-01 | PASS | 89 | 6.52 |
| real-onboard-restart-while-waiting-operator-01 | PASS | 124 | 7.7 |
| real-onboard-cancellation-authorization-lost-01 | PASS | 101 | 7.74 |
| real-onboard-durable-ack-lost-01 | PASS | 104 | 5.87 |
| real-onboard-durable-ack-lost-02 | PASS | 98 | 5.54 |
| real-onboard-durable-ack-lost-03 | PASS | 94 | 6.29 |
| real-onboard-expected-action-overdue-01 | PASS | 111 | 5.69 |
| real-onboard-expected-action-overdue-02 | PASS | 117 | 5.96 |
| real-onboard-expected-action-overdue-03 | PASS | 110 | 6.36 |
| real-onboard-order-hang-continue-01 | PASS | 50 | 6.15 |
| real-onboard-restart-with-open-recovery-session-01 | FAIL (1) | 84 | 6.31 |
| real-onboard-restart-after-recovery-session-opened-01 | PASS | 72 | 6.48 |
| real-onboard-mixed-side-one-stop-01 | PASS | 247 | 6.65 |
| real-onboard-refilled-deadline-reaches-vehicle-01 | PASS | 57 | 6.9 |
| real-onboard-rebuild-stopped-cargo-handoff-01 | PASS | 116 | 6.78 |
| real-onboard-cancelled-rebuild-cargo-proof-01 | PASS | 179 | 5.77 |
| real-onboard-stale-stop-after-station-timeout-01 | PASS | 271 | 7.02 |
| real-onboard-in-transit-door-facts-01 | PASS | 152 | 6.47 |
| real-onboard-unread-pre-create-read-01 | PASS | 85 | 6.39 |
| real-onboard-slot-fault-declaration-01 | PASS | 77 | 6.01 |
| real-onboard-slot-fault-declaration-02 | PASS | 79 | 6.07 |
| real-onboard-slot-fault-declaration-03 | PASS | 70 | 5.64 |
| real-onboard-charging-cycle-01 | PASS | 138 | 5.37 |
| real-onboard-charging-cycle-02 | PASS | 126 | 5.33 |
| real-onboard-charging-cycle-03 | PASS | 123 | 4.16 |
| real-onboard-manual-charging-hold-return-01 | PASS | 46 | 4.67 |
