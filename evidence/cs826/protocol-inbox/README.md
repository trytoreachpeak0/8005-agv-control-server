# ProtocolInbox / ProtocolOutbox read, agv02, 2026-10-21 02:00-09:40 CST

Approved once by the user through Coordinator13 (2026-10-21). One `Mode=ReadOnly;Pooling=False` connection to the factory01
v2 database, two SELECTs (`ProtocolInbox` `PreDepartureSafetyCheckResult`, `ProtocolOutbox` `PreDepartureSafetyCheck`), rows
filtered to agv02 after reading; DLLs from the sqlite-tools copy. Opened 22:16:40.065, closed 22:16:40.300 (+08:00). The script,
the command, the raw output and the analysis script stay in `C:/work/8005-scratch/cs826/`; `timeline.txt` here is the analysis.

## What the rows show

- 74 checks asked, 74 answers, **one answer per check id** in every case. All 74 answers are `SAFE`, with every safety flag
  true, the session generation and the safety state version the check asked for, and `validUntil = observedAt + 2 s`.
- The answer arrives 23-50 ms after the check (the check's `CreatedAt` to the answer's `ReceivedAt`, both server clock).
- **`observedAt` is later than the server's `ReceivedAt` in 70 of 74 answers**: in the four stuck stretches by +83 to +113 ms.
  The answer's own time is in the server's future when it is received.
- Every check falls in one of two kinds. 14 were consumed (`AcknowledgedAt` set -- the engine settles the check when the vehicle
  departs on its answer -- and no `FencedAt`). 60 were never consumed and were retired (`FencedAt` set, no `AcknowledgedAt`);
  each retired check was followed 33-37 s later by a new check id (`validUntil` 2 s + the evidence age, as
  `ReissueExpiredDepartureCheckAsync` does). Retired per stretch: 02:28-02:30 4; 08:35-08:37 2; 08:48-08:57 16; 09:08-09:30 38.
  An unconsumed check is replayed to the vehicle until it is retired, which fits the onboard log's
  `BUSINESS_ID_CONTENT_CONFLICT` every ~6 s (the onboard side refused to answer the same check id again; onboard-hmi#386).
- Each stretch ended on a consumed check: 02:30:54 (+113 ms), 08:37:28 (+52 ms), 08:57:38 (+15 ms), 09:30:36 (-24 ms, after
  agv02's clock was stepped back 0.116 s at 09:30:31).

## Against the ticket's inference

- **Confirmed**: the cause is `observedAt <= now` with zero tolerance. The engine judges the answer within one 50 ms wait step
  of receiving it; an answer stamped ~0.1 s ahead is "in the future" at that moment and is invalid.
- **How 08:57 (and 02:30, 08:37) ended**: no clock change. The answer happened to be judged later than its `observedAt` --
  at 08:57:38 the offset was only +15 ms (the 0.12 s offset jitters by tens of ms with the onboard stamping); at 02:30:54 and
  08:37:28 the judgment evidently fell after the offset. Departing was a matter of timing, not of the vehicle's state.
- **Not confirmed here**: "the first invalid answer hides later valid ones" did not happen on 10-21 -- each check id got exactly
  one answer, because the onboard side did not re-answer a repeated check (`BUSINESS_ID_CONTENT_CONFLICT`, fixed by
  onboard-hmi#386). The defect was latent; it becomes reachable now that the onboard side re-answers, which is why this
  ticket still fixes it.
