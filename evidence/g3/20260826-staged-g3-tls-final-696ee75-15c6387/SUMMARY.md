# Deterministic real-Onboard TLS recovery replay result

Result: `STAGED_G3_TLS_RECOVERY_REPLAY_PASS`.

This is a deterministic no-movement staged G3 vector. It closes the retained
real-Onboard-plus-TLS uncertainty for `RecoveryStateReport` first-Ack-drop
replay. It is not a formal W2G-IS-00 or W2G-IS-06 PASS, not a complete G3 PASS,
and not an RC PASS.

## Exact inputs

- ControlServer runner/product: `ControlServer_MVP@696ee75525ea855adfb6fcc807f7feda013c6668`.
- OnboardHmi tested tree: `OnboardHmi_MVP@15c6387801fa2154fb69441eac460fea9d0999c5`.
- OnboardHmi product fix ancestor: `0584322e86bdf6e1f94b58a70381b42d353ba5df`, authored by Kun Wang.
- slots-simulator: `main@fb5f7c593742bf98bc3957b8729a38aad5321f28`.
- protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.
- protocol manifest SHA-256: `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`.
- configuration SHA-256: `0beb62daa66efe1750fb3ccd9ee92c46ca7e60ea54159772f5232a91d693251a`.
- `run-result.json` SHA-256: `5647b661223c9ed11f6363c143567614942f52bef4e4e3a768fc28a433290068`.

## Result

- Protocol G1: PASS.
- Real TLS release, manifest, and credential rejection probes: PASS.
- Same connection, same `messageId`, same content byte-exact response replay: PASS.
- Same `messageId`, different content stable connection-close conflict: PASS.
- Real OnboardHmi TLS recovery replay: PASS. Generation 1 sent one
  `RecoveryStateReport`; the proxy dropped its first `DurableAck` and closed
  the TLS connection. Generation 2 replayed the same message ID and the same
  business-payload SHA-256 over a new TLS connection and accepted the forwarded
  Ack.
- The replay converged to the safe fail-closed state
  `RecoveryRequired / CAPABILITY_SNAPSHOT_REQUIRED` at generation 2.
- No RIoT order, vehicle movement, accepted Demand, or station operation was
  created. Secret scan: PASS.

## Temporary trust boundary

With the user's explicit authorization, the runner installed one uniquely
named test root into `CurrentUser/Root`. The real Onboard still required both a
valid Windows certificate chain and the exact leaf SHA-256 pin. The runner
removed the PFX and exact root in `finally`; machine evidence records
`temporaryTrustCleanupVerified=true`. Post-run inspection found zero staged G3
roots and no staged listener on ports 58205, 58207, 58215, 1502, or 58006.

## Classification

The real-Onboard TLS replay vector is closed. `formalSlicePass=false` remains:
the wider W2G-IS-00 through W2G-IS-07 field prerequisites, complete G3, and RC
were not exercised and remain `INCONCLUSIVE`.
