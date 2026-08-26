# Deterministic staged G3 split-transport result

Result: `STAGED_SLICE_INCONCLUSIVE_TLS_COMBINATION`.

This run is a deterministic, no-movement staged slice. It is not a formal
W2G-IS-00 or W2G-IS-06 PASS, not a complete G3 PASS, and not an RC PASS.

## Exact inputs

- ControlServer: `b4afdb3ce3d6ee2b84b78b09101af689f1cbde92`
- OnboardHmi evidence binding: `15c6387801fa2154fb69441eac460fea9d0999c5`
- OnboardHmi runner binding: `4d8629c158a02101819d475c1a2a8610b333cb48`
- OnboardHmi product fix: `0584322e86bdf6e1f94b58a70381b42d353ba5df`
- slots-simulator: `fb5f7c593742bf98bc3957b8729a38aad5321f28`
- protocol: `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`
- manifest SHA-256: `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- configuration SHA-256: `df3e18bf373e5d506b99288f02ade56d3a174b24d7ee3df05c621d78748e08d4`
- `run-result.json` SHA-256: `615dc2a6e4d37abf2654a75e563dcbbe1dc2820453806f9c2b12f29b9219e9e4`

The full clone, fetch, checkout, G1, and publish commands and their log hashes
are recorded in `run-result.json`.

## Deterministic results

- Protocol G1: PASS.
- Real `SslStream` loopback TLS probe: PASS.
- Release identity rejection: PASS.
- Manifest identity rejection: PASS.
- Credential identity rejection: PASS.
- Same connection, same `messageId`, same content: byte-exact replay PASS.
- Same `messageId`, different content: stable connection-close conflict PASS
  on the original and a new session.
- Real OnboardHmi `RecoveryStateReport` first Ack drop: PASS over plaintext
  loopback. The same message ID and payload hash were sent twice across two
  connections and session generations 1 and 2; the replay Ack was forwarded.
- OnboardHmi Ack-drop over TLS: INCONCLUSIVE because no existing supported,
  trusted loopback server certificate with an accessible private key was
  available. No root trust prompt was bypassed and no certificate-store
  mutation was performed.
- No real RIoT order, movement command, fabricated eligibility, external
  credential, or Golden WPF run occurred. Business table counts remained zero.
- Secret scan: PASS.

## Classification and retained red evidence

`formalSlicePass=false`; W2G-IS-00 and W2G-IS-06 remain `INCONCLUSIVE`; full G3
and RC remain `INCONCLUSIVE`.

The prior red evidence remains preserved in OnboardHmi at
`a1e32dd/evidence/g3/20260826-recovery-ack-drop-cc6e2b9-0455147/SUMMARY.md`.
