# control-server#798 synthetic L2

- pass/: run 20261021T165443838Z, control-server 3df9bc59e plus the scenario files later committed unchanged as 2672a38c6. PASS 3/3.
- red-m1/: same scenario with mutation M1 applied (the stood-on check in IdleReturnEvaluator.ExcludePointAsync disabled: `&& origin < 0`). L2-CSO-02 FAIL: A committed to 214 while B stood on it (CHARGING_CLEARANCE_TO_WAITING_POINT, 1 clearance intent, 214 RESERVED by A).
