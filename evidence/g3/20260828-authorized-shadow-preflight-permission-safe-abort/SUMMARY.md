# Authorized shadow preflight: permission-assumption safe abort

Result: **SAFE_ABORT_BEFORE_HOST**. The authorization stated that it would be
consumed only when the isolated Host started. The Host package was not copied,
no Host or peer process started, no shadow database or permit was created, and
no proxy or RIoT request occurred. The authorization was therefore not
consumed, although the corrected runner has a new identity and must receive a
new hash-bound authorization before use.

Two preflight-tool assumptions caused the abort. The unelevated runner could
not byte-hash the installed `Program Files` configuration, and its failure
path attempted to reapply an already-correct restricted ACL, which required
`SeSecurityPrivilege`. The retained run root remained protected with exactly
three explicit full-control entries: the current user, SYSTEM, and
Administrators.

The runner now compares a canonical SHA-256 of all stable fields emitted by
the existing fixed elevated read-only effective-state task before and after
the shadow. That gate strictly requires schema/result, both runtime flags,
threshold, service state/start mode/account, required port ownership, health,
and broker identity. This proves the safety-relevant installed effective state
is unchanged; it does not claim byte identity for the two installed config
files. The final evidence path now verifies the existing restricted ACL
without rewriting it.

Validation passed: PowerShell AST parsing; two consecutive effective-state
inspections produced the same canonical hash; native JSON booleans passed and
the string `"false"` was rejected; the retained ACL passed the exact-rule
assertion. Independent safety review returned GO for a future
mutation-blocked shadow using the final clean runner identity.
