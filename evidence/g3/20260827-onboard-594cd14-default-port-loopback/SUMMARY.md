# Onboard 594cd14 default-port loopback preflight

Date: 2026-08-27 (Asia/Shanghai)

Result: default-port loopback transport/provider preflight PASS; the session correctly remains `RecoveryRequired`, and
formal W2G-IS-00 through W2G-IS-07 G3/RC remains `INCONCLUSIVE`.

## Bound identities

- Deployed ControlServer product: `ControlServer_MVP@5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- ControlServer integration evidence base: `ControlServer_MVP@f89ec2d48de29351c00f929e1fdb3e2f7751a173`
- Protected Onboard remote, inspected and validated read-only: `OnboardHmi_MVP@594cd14e2dec17285dc1352134515e3cab4abfd2`
- Protected slots-simulator remote, run from a disposable clone: `main@fb5f7c593742bf98bc3957b8729a38aad5321f28`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`

The protected Onboard and slots-simulator worktrees were not edited. Build output, runtime configuration, journal,
logs, and simulator state existed only in disposable copies outside those repositories.

## Read-only fix validation

The Onboard owner changed both checked-in configuration examples and `WireToGateSettings` to use the authoritative
ControlServer transport port 58005. In a disposable clone at the exact commit, the VSTest/xUnit v2 selection covering
the port regression and all ControlServer HTTPS vehicle-safety provider tests passed 18/18 with zero failures and zero
skips. The complete Onboard solution Release build passed with zero warnings and zero errors.

## Authorized loopback run without a port override

The user explicitly authorized one local Onboard plus IO-simulator run that could write the ControlServer session
recovery row, a disposable Onboard journal, and simulator state. The scope excluded JourneyRuntime enablement, RIoT
mutation, order creation, vehicle movement, and access to the vehicle touchscreen.

The runner consumed the Release output's checked-in port 58005 and contained no assignment overriding
`wireToGate.port`. It used the installed private localhost trust chain and the Machine-scope Onboard credential without
printing, hashing, or persisting the credential. The disposable configuration supplied the exact live vehicle identity
without placing it in Git or evidence.

Observed results:

- the simulator health endpoint reported `READY` on loopback Modbus/HTTP ports;
- Onboard established the TLS/NDJSON connection to the deployed ControlServer on port 58005;
- both the ControlServer runtime row and Onboard log observed session generation 2 as `RecoveryRequired`;
- the stable reason was `DEPARTURE_SAFETY_NOT_READY`;
- the authenticated HTTPS provider and the session safety outcome agreed on fail-closed `UNKNOWN` with
  `RIOT_MOVEMENT_NOT_FINISHED`;
- the ControlServer service remained running with the same process, while the Onboard and simulator processes were
  stopped and temporary ports 1502 and 58006 were released.

This closes the static and runtime uncertainty around the Onboard transport-port correction. It does not produce a
matching `READY` session: the remaining readiness blocker is the external RIoT motion predicate, not port selection,
TLS, authentication, JSON mapping, or provider wiring.

## Remaining blockers

- RIoT still reports `RIOT_MOVEMENT_NOT_FINISHED`, so the session correctly cannot become `READY`.
- The current MES/Map snapshot still contains a missing Map station and unapproved PACKAGE capacity mappings; these
  dynamic facts must be reread atomically before a formal run.
- No authorization was granted for RIoT mutation, order creation, or vehicle movement.
- Formal W2G-IS-00 through W2G-IS-07 G3 and the release candidate remain `INCONCLUSIVE`.
