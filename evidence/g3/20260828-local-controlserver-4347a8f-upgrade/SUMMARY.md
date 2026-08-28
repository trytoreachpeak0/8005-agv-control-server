# Local ControlServer 4347a8f upgrade

Observed: 2026-08-28, Asia/Shanghai

Result: PASS. This artifact records a local product upgrade only; it does not authorize or claim a W2G journey.

## Bound identities

- Deployed product source: `ControlServer_MVP@4347a8fb9fcb80cb9f95680a6fd8b1a0b970358b`
- Product/evidence parent: `ControlServer_MVP@5ebd4a01d21624a92d0bc58fb5828a2ee2f4ba3e`
- Package manifest SHA-256: `29abd09e017636ae4eb7d09534400ca137fcaea01ec87b7710efddef78a3ca24`
- Package runtime identity: self-contained `win-x64`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`

The deployment source was a clean detached disposable clone at the exact product commit. Its package manifest declared 371 payload files; an independent pre-deploy pass found zero missing files or SHA-256 mismatches.

## Upgrade execution

The existing `Update-ControlServerLocal.ps1` upgrade path was used with authenticated read-only safety verification enabled. It validated the package manifest, required `JourneyRuntime.enabled=false`, created ACL-restricted install/data backups, staged and atomically replaced the install tree while preserving production configuration, and ran service/live/version/restart/safety checks. Its sanitized result returned:

- result `PASS`;
- source commit `4347a8fb9fcb80cb9f95680a6fd8b1a0b970358b`;
- service `Running`;
- authenticated safety projection `STOPPED`;
- JourneyRuntime disabled;
- no RIoT mutation, order creation, or vehicle movement.

The exact backup location remains in the ACL-protected external upgrade result and is intentionally not duplicated in Git.

## Independent post-upgrade checks

- Windows service `8005 AGV ControlServer`: Running / Automatic / LocalSystem.
- The new service process owns loopback TLS ports 58005 and 58007.
- `/version` reports `protocol-v0.1.1`, commit `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`, profile `WIRE_TO_GATE_MVP`.
- A fresh atomic read-only preflight observed vehicle IDLE, speed zero, no order, battery 30%, and matching direct/HTTPS `STOPPED` projections with zero reason codes.
- JourneyRuntime remained disabled; Onboard and simulator were not started.

The installed directory correctly denies ordinary-user manifest reads through its production ACL. Product identity is bound by the elevated updater's validated manifest and sanitized PASS result; no ACL or secret was weakened for independent inspection.

## Remaining gate

The ControlServer side of the safe-revision Ready transition is now deployed. Formal G3/RC remains `INCONCLUSIVE`: the next step is a new, separately authorized guarded run using protected-owner `OnboardHmi_MVP@777eff8bdc955e6bb6fdab74ec222e0bb6748def`. This deployment does not reuse any earlier vehicle authorization.

Machine-readable sanitized facts are in the adjacent `result.json`. Raw configuration, credentials, backup paths, candidate identities, vehicle identity, response bodies, and diagnostic logs are excluded.
