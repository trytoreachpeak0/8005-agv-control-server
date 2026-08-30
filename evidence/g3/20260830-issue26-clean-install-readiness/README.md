# Issue 26 clean-install readiness control

Ticket 13's clean RC run did send and durably persist `RecoveryStateReport`. Its final
`SessionRecoveries` row was `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`, with
`DepartureSafe=0`; the ticket's original "report was not sent" hypothesis is false.

`captured-handshake.json` is the real ticket 13 handshake read back from its SQLite inbox. The
credential is redacted. `Invoke-ReadinessControl.ps1` replays that handshake twice against the
`d243abf` RC with fresh databases and both journey and RIoT mutation disabled:

- red: captured safety facts unchanged;
- green: only `payload.safety.vehicleStopped` and `departureSafe` become `true`, and the old
  safety reason is cleared.

`assertions.json` was emitted by that run: red is `RECOVERY_REQUIRED /
DEPARTURE_SAFETY_NOT_READY`; green is `READY`; both receive a `RecoveryStateReport` DurableAck.
No vehicle movement or RIoT mutation occurred.

The deployment difference is configuration, not publish shape. Ticket 13 copied the RC development
`appsettings.json`, enabled only WIRE_TO_GATE, and retained `vehicleSafety.enabled=false`. The public
RC procedure requires replacing that file with `appsettings.Production.template.json`, enabling the
ControlServer HTTPS safety projection, trusting its CA on the Onboard host, and obtaining a fresh
`STOPPED` observation for the configured vehicle key. `docs/RELEASE-CANDIDATE.md` now makes those
readiness prerequisites explicit.
