# Authorized mutation-blocked shadow: extractor-window safe abort

Result: **SAFE_ABORT / PERMIT_NOT_ISSUED**. The exact authorized proxy,
isolated Host, Onboard, and simulator started, so the one-shot authorization
was consumed. Session generation 1 reached `Ready / READY`; the isolated
runtime accepted exactly one demand and persisted one journey runtime, one
`TO_PICKUP` intent, and one matching active vehicle lease. No private permit
was issued because the authorized extractor rejected the final audit shape.

The egress boundary forwarded 169 allowlisted GETs and zero mutations. It saw
83 station reads, 10 vehicle reads, and 76 order reconciliations. There were no
blocked reads, blocked mutations, forwarded mutations, create attempts,
orders, experimental authorizations, or station operations. Thirty-eight
independent safety samples remained `STOPPED` with zero reasons; vehicle
movement was not observed. The production database and the complete
safety-relevant installed effective-state hash were unchanged, cleanup had no
unknown process or port residue, and the installed runtime remained disabled.

The sole failure was an extractor timing assumption. The first exact
`PRE_CREATE_RECONCILIATION / UNKNOWN / RECONCILE / AbsentAtObservation`
audit was followed about one second later by the runtime's normal read-only
post-create reconciliation. By shutdown the same intent had one exact PRE and
75 exact POST absence audits, all on one identity with contiguous sequence and
no attempt, order, authorization, HTTP/business result, or failure. The old
extractor required the whole audit table to contain exactly one row, so its
one-second external-process polling could miss the short window and never
recover.

The corrected offline extractor accepts one exact PRE followed only by zero or
more identity-equal, contiguous, mutation-free POST absence reads. It also
requires a positive integer-zero create-attempt count; null/legacy unknown is
rejected. Any create phase, attempt, arm, request hash, order, authorization,
station operation, identity drift, sequence gap, or receipt deviation still
fails closed. In-memory validation accepted the stopped 76-audit database and
the single-PRE shape, while rejecting an injected create phase and null attempt
count. Independent security review selected stopped-state offline extraction
over a transient high-frequency watcher. No offline permit extraction has yet
been authorized or performed.
