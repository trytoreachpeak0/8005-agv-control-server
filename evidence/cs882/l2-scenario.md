# cs#882 L2: charging-order-not-dispatched-holds-charger

CI l2 run 42155903442 on head 6766750ec: 114/115 PASS, this scenario FAIL (1). The scenario is control-server#863's; its L2-COND-02 still asserted the pre-cs#882 behaviour (cancel -> ORDER_ENDED_WITHOUT_ARRIVAL, 211 kept, manual clearance frees it). Not a product fault.

## Local run of the unchanged scenario on 6766750ec (reproduces the CI red)
```
expected: ORDER_ENDED_WITHOUT_ARRIVAL | RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261106T035226141Z / CONFIRMED released=1 / RESERVED BROKERX-L2-0002 | CHARGING / 1 main intents, 0 rebuilds
actual:   CHARGING_ORDER_ENDED_UNDISPATCHED | RESERVED BROKERX-L2-0002 charging:BROKERX-L2-0002:20261106T035259171Z / REJECTED released=0 / RESERVED BROKERX-L2-0002 | CHARGING / 1 main intents, 0 rebuilds
```
The actual reading is the new behaviour: the undispatched ending, 211 already handed to the second vehicle, and the clearance REJECTED because nothing was left to clear. One 2286 line in the server log.

## After rewriting L2-COND-02 to the new behaviour
```
L2 charging-order-not-dispatched-holds-charger -> PASS (evidence: C:\work\8005-scratch\cs882\l2-local-green)
expected: CHARGING_ORDER_ENDED_UNDISPATCHED | 1 endings | 211 not BROKERX-L2-0001: True | main (none) | 0 order commands / RESERVED BROKERX-L2-0002 | CHARGING / 1 main intents, 0 rebuilds
actual:   CHARGING_ORDER_ENDED_UNDISPATCHED | 1 endings | 211 not BROKERX-L2-0001: True | main (none) | 0 order commands / RESERVED BROKERX-L2-0002 | CHARGING / 1 main intents, 0 rebuilds
```

Scanning guards (retired-name guard and every test class that reads scripts/, plus ArchitectureTests and ~L2): 已通过! - 失败:     0，通过:   422，已跳过:     0，总计:   422，持续时间: 44 s
