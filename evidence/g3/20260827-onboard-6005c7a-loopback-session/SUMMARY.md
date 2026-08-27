# Onboard 6005c7a loopback session and vehicle-safety provider preflight

Date: 2026-08-27 (Asia/Shanghai)

Result: loopback transport/provider preflight PASS; the session correctly remains `RecoveryRequired`, and formal
W2G-IS-00 through W2G-IS-07 G3/RC remains `INCONCLUSIVE`.

## Bound identities

- Deployed ControlServer product: `ControlServer_MVP@5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- ControlServer integration evidence base: `ControlServer_MVP@ab8aab684ae9319cb29af60af3d4afa223f11b64`
- Protected Onboard remote, inspected and validated read-only: `OnboardHmi_MVP@6005c7a89558593f199b04b1810426bc2f4072ae`
- Protected slots-simulator remote, run from a disposable clone: `main@fb5f7c593742bf98bc3957b8729a38aad5321f28`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`

The protected Onboard and slots-simulator worktrees were not edited. Restore/build output and runtime configuration
were created only in disposable copies outside those repositories.

## Read-only source and focused validation

The new Onboard commit is authored and committed by Kun Wang and replaces the unavailable vehicle-safety provider in
the WPF composition root with `ControlServerVehicleSafetySignalProvider`. The provider uses the ControlServer HTTPS
projection, a Bearer credential read from the named environment variable, normal Windows certificate trust and host
validation, exact vehicle identity, evidence freshness, and fail-closed motion mapping.

In the disposable Onboard copy, the VSTest/xUnit v2 selection covering `ConfigurationTests` and
`ControlServerVehicleSafetySignalProviderTests` passed 25/25 with zero failures and zero skips. Release builds of the
Onboard WPF application and slots simulator both passed with zero warnings and zero errors.

## Authorized loopback run

The user explicitly authorized one local Onboard plus IO-simulator run that could write the ControlServer session
recovery row, a disposable Onboard journal, and simulator state. The scope excluded JourneyRuntime enablement, RIoT
mutation, order creation, and vehicle movement.

The runtime used the installed private localhost trust chain and the rotated Machine-scope Onboard credential without
printing, hashing, or persisting the credential. The disposable configuration supplied the exact live vehicle identity
without placing it in Git or evidence.

Observed results:

- the simulator health endpoint reported `READY` on loopback Modbus/HTTP ports;
- Onboard established the TLS/NDJSON connection to the deployed ControlServer on port 58005;
- both the ControlServer runtime row and Onboard log observed session generation 1 as `RecoveryRequired`;
- the stable reason was `DEPARTURE_SAFETY_NOT_READY`;
- the authenticated HTTPS provider and the session safety outcome agreed on fail-closed `UNKNOWN` with
  `RIOT_MOVEMENT_NOT_FINISHED`;
- the Onboard and simulator processes were stopped, and temporary ports 1502 and 58006 were released.

This closes the earlier uncertainty about whether the protected Onboard head contains a usable production-shaped
HTTPS provider and can connect to the deployed ControlServer. It does not produce a matching `READY` session: the
remaining readiness blocker is the external RIoT motion predicate, not TLS, authentication, JSON mapping, or provider
wiring.

## Protected-owner configuration defect

At `OnboardHmi_MVP@6005c7a`, both `src/SQCD.Agv.Wpf/appsettings.json` and
`src/SQCD.Agv.Wpf/appsettings.Production.example.json` still set `wireToGate.port` to 58015. The ControlServer source
of truth and installed service listen on 58005. The loopback run required an explicit disposable override to 58005.

This is owned by the protected `8005-agv-onboard-hmi` repository. Agents must not edit or create tracker content there.
This integration evidence is the writable routing record; the human Onboard owner must change the two configuration
defaults to 58005 (or explicitly establish a different shared port) and publish a new confirmed commit before field
configuration is copied from the example.

## Remaining blockers

- RIoT still reports `RIOT_MOVEMENT_NOT_FINISHED`, so the session correctly cannot become `READY`.
- The current MES/Map snapshot still contains a missing Map station and unapproved PACKAGE capacity mappings; these
  dynamic facts must be reread atomically before a formal run.
- No authorization was granted for RIoT mutation, order creation, or vehicle movement.
- Formal W2G-IS-00 through W2G-IS-07 G3 and the release candidate remain `INCONCLUSIVE`.

