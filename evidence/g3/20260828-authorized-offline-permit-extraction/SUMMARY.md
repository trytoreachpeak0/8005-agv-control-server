# Authorized stopped-state offline permit extraction

Result: **PASS**. The explicitly authorized offline extractor ran against the
stopped mutation-blocked shadow database. Before execution it reverified the
clean tool commit, state-tool hash, Python executable and runtime tree, original
run-result hash, stopped shadow DB/WAL/SHM bundle, read-only production overlap
input bundle, exact restricted ACL, absent output paths, and zero temporary
listeners or peer/helper processes. All bound inputs were reverified after the
extractor exited.

The stopped state contained exactly one accepted demand, one journey runtime,
one `TO_PICKUP` intent, and one matching active lease. Its 76 audit events were
one exact pre-create `AbsentAtObservation` followed by 75 identity-equal,
contiguous, mutation-free post-create reconciliation reads. Create-attempt
count was positively zero; there was no create phase, arm, request hash, order,
experimental authorization, station operation, identity drift, sequence gap,
or receipt deviation. The selected identity did not overlap the read-only
production database.

The extractor wrote only the authorized private permit and sanitized result
inside the existing restricted run root. Their selection hashes match. The
private permit remains local and is not reproduced in repository evidence;
the repository records only hashes and aggregate facts. No Host, proxy,
Onboard, or simulator started, and no RIoT request occurred.

This permit is an identity-bound input for a possible later experiment. Its
existence does not authorize a RIoT mutation, order creation, or vehicle action;
those require a separate one-shot authorization and an exact real-egress gate.
