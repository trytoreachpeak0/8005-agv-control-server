# control-server#798 synthetic L2

- pass/: run 20261021T165443838Z, control-server 3df9bc59e plus the scenario files later committed unchanged as 2672a38c6. PASS 3/3.
- red-m1/: same scenario with mutation M1 applied (the stood-on check in IdleReturnEvaluator.ExcludePointAsync disabled: `&& origin < 0`). L2-CSO-02 FAIL: A committed to 214 while B stood on it (CHARGING_CLEARANCE_TO_WAITING_POINT, 1 clearance intent, 214 RESERVED by A).
- rework-pass/: after the review rework, control-server 56300ba1b. One run; PASS on all three criteria (L2-CSO-02 now names the vehicle and source: 214=WAITING_POINT_STOOD_ON(AGV-L2-002,read)).
- rework-red-m1/: 56300ba1b with mutation M1 applied. One run; L2-CSO-02 FAIL, the other two PASS.
- Every directory here is one run; "3/3" in the first round meant three criteria, not three runs.
