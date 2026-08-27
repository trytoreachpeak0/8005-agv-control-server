# Onboard fresh-journal SafetyStateChanged message identity collision

Date: 2026-08-27 (Asia/Shanghai)

Result: authorized empty-journey G3 stopped before order creation because a real Onboard session could not remain
stable after its first post-Ready `SafetyStateChanged`. Formal W2G G3 remains `INCONCLUSIVE` with a core session
failure.

## Bound identities and authorization

- Deployed ControlServer product: `ControlServer_MVP@5c726218f59d10d5929e7f5c0a39c1ebd4df52f1`
- Protected Onboard owner commit, inspected and executed from a disposable copy only:
  `OnboardHmi_MVP@594cd14e2dec17285dc1352134515e3cab4abfd2`
- Protected simulator commit, executed from a disposable copy only:
  `main@fb5f7c593742bf98bc3957b8729a38aad5321f28`
- Protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- Machine-readable result SHA-256: `ba3eaa0d0de340662192ac70ff29c941045af21feed32f5edfc0268eaef1906a`
- The user explicitly authorized one empty Map 25 journey, including temporary JourneyRuntime enablement, RIoT order
  creation, and real vehicle movement. Real material was excluded.

The final atomic preflight found 13 statically ready WIRE_TO_GATE items. The vehicle was IDLE, speed zero, without an
order, and `STOPPED`; battery was 15% against the user-authorized local test threshold of 10%.

## Reproduction

Two guarded sequences reproduced the same boundary before any RIoT mutation:

1. With peers already Ready, enabling JourneyRuntime restarted ControlServer. Onboard generation 5 had been `Ready`,
   but its in-flight `SafetyStateChanged` lost the durable Ack when the connection closed. Subsequent recovery sessions
   remained `HANDSHAKE_INCOMPLETE`.
2. To remove restart ordering as a variable, JourneyRuntime and the sanitized database monitor were started first;
   the new Onboard and simulator were then launched from fresh disposable runtime directories. Generation 25 reached
   `Ready`, but the first `SafetyStateChanged` immediately observed an end-of-stream before `DurableAck`. Reconnects
   advanced through at least generation 38 and remained `RecoveryRequired / HANDSHAKE_INCOMPLETE`.

No `JourneyRuntime`, backlog, order intent, or station operation row was created. The vehicle never moved.

## Owner diagnosis

Read-only source inspection strongly identifies an Onboard durable-message identity collision:

- `WireToGateSessionClient.SendSafetyStateChangedAsync` derives the deduplication key solely as
  `safety-state-changed:{safetyStateVersion}` and derives `messageId` with `StableUuid` from that key.
- `WireToGateBusinessService` starts its in-process `_nextSafetyStateVersion` sequence from the initial value for every
  fresh runtime/journal.
- Therefore different fresh journals reuse the same stable `SafetyStateChanged` messageId for version 1 while
  `observedAt` and the payload content change. ControlServer's durable inbox is globally keyed by messageId and rejects
  a reused identity with different content. The observed immediate close before Ack and subsequent recovery loop match
  that conflict exactly.
- Wang Kun's earlier `0584322` fix correctly rebinds pending messages across session generations within one durable
  journal, but it does not make identities unique across fresh journals or installations.

The owning repository is protected and was not edited. The likely owner symbols are:

- `src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs`
- `src/SQCD.Agv.Wpf/WireToGateBusinessService.cs`
- the SQLite journal implementation that persists durable outgoing identities

The owner fix must preserve one identity for retries of the same durable work while ensuring different fresh-journal
events cannot reuse that identity. Regression coverage should connect two fresh journals sequentially to the same
durable server, send safety version 1 with different observations, require distinct messageIds, and separately prove
that a lost Ack in one journal still replays the identical message identity and content.

## Safe cleanup

The elevated orchestrator was stopped and verified JourneyRuntime disabled. Onboard and simulator were stopped,
ports 1502 and 58006 were released, and ControlServer remained Running. Final read-only RIoT observation was IDLE,
speed zero, no order, and `STOPPED`. No RIoT mutation, order creation, or vehicle movement occurred.

Do not retry the real journey with a fresh journal until the protected Onboard owner publishes and confirms a fix.
