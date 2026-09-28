cs#366 CI 真装置（调度 Coordinator 8 放的持有，2026-09-28），对端 onboard 4c2d2dc14656f80e812f37128a7964c2c310217a、simulator fb5f7c593742bf98bc3957b8729a38aad5321f28，mode=consecutive，各 1 遍。

| run | 服务端（日志「Run real-onboard L2 scenarios」那一行读到的） | 场景 | 结论 |
| --- | --- | --- | --- |
| 36390242195 | 9e7719221af503bcef2067c33477a83a62ec1e48 | real-onboard-compensate-then-reconnect | PASS 78s，L2-CR-00～08 |
| 36390242195 | 同上 | real-onboard-rebuild-stopped-cargo-handoff | PASS 92s，L2-RH-01～11 |
| 36390242195 | 同上 | real-onboard-cancelled-rebuild-cargo-proof | FAIL 208s：场景脚本的错，见 run-36390242195/README.txt |
| 36391415668 | 4283e5ecb1c8f360d2975544103de51efa5abc20 | real-onboard-cancelled-rebuild-cargo-proof | PASS 134s，L2-RC-01～11 |

四行核对：三端提交都从日志那一行读，与派发一致；两轮里 RIG_COMMIT_GUARD / RIG_DESKTOP_LOCK / RIG_DEADLINE / NOT_STARTED 的命中都带 ^[[36;1m（源码回显），
其余行 0 命中；每个场景都有带耗时的 PASS/FAIL 行。artifact 两轮都非空（2043975、332904 字节）。
9e771922 → 4283e5ec 只改了 scripts/l2/scenarios/real-onboard-cancelled-rebuild-cargo-proof.ps1 与 evidence/，产品代码（src/）逐字节相同，
所以第一轮另外两个场景的 PASS 对 4283e5ec 仍然作数。
