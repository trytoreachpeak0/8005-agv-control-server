# Deterministic staged G3 rerun after Onboard recovery replay fix

Result: `STAGED_G3_BLOCKER_VECTOR_PASS_TLS_COMBINATION_INCONCLUSIVE`.

This is a deterministic no-movement staged rerun. It closes the previously
red `RecoveryStateReport` first-Ack-drop replay vector, but it is not a formal
W2G-IS-00 or W2G-IS-06 PASS, not a complete G3 PASS, and not an RC PASS.

## Exact inputs

- ControlServer: `ControlServer_MVP@8952603bffd9ff63858881fc8d38f8acf2c75d8a`
  (contains runner/product ancestor
  `b4afdb3ce3d6ee2b84b78b09101af689f1cbde92`).
- OnboardHmi tested tree:
  `OnboardHmi_MVP@15c6387801fa2154fb69441eac460fea9d0999c5`.
- OnboardHmi product fix:
  `0584322e86bdf6e1f94b58a70381b42d353ba5df`, authored by Kun Wang; it is an
  ancestor of the tested tree through runner binding `4d8629c`.
- slots-simulator: `main@fb5f7c593742bf98bc3957b8729a38aad5321f28`.
- protocol:
  `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.
- manifest SHA-256:
  `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`.
- configuration SHA-256:
  `3c68055f338d92182e5ab2989a910b07759c84cbe358fb840b6a46aaac380d89`.
- `run-result.json` SHA-256:
  `d5fc6f05a10ffd3b26b3fb1aaecd818ef641096138a50bc6d8b1a4a90910b0a5`.

## Onboard qualification

The protected Onboard repository was cloned to a disposable directory with
`core.autocrlf=false`; the bundled Node/pnpm runtime was placed first on PATH.
The official G2 evidence entry ran protocol G1, Release build, the complete
solution tests, and format verification:

- protocol G1: PASS;
- Release build: PASS, 0 warnings / 0 errors;
- unit tests: 62/62 PASS, 0 skipped;
- W2G G2: 13/13 PASS, 0 skipped;
- format: PASS.

The evidence wrapper then stopped during summary post-processing because the
exact commit was checked out detached and `git branch --show-current` returned
no value. This did not change any gate result and is explicitly retained as an
evidence-wrapper limitation in `onboard-qualification/result.json`; it is not
misreported as a complete wrapper PASS.

## Deterministic staged results

- Protocol G1: PASS.
- Real `SslStream` loopback TLS probe: PASS.
- Release identity rejection: PASS.
- Manifest identity rejection: PASS.
- Credential identity rejection: PASS.
- Same connection, same `messageId`, same content: byte-exact replay PASS.
- Same `messageId`, different content: stable connection-close conflict PASS
  on the original and a new session.
- Real OnboardHmi `RecoveryStateReport` first Ack drop: PASS over plaintext
  loopback. Exactly two reports crossed exactly two connections and session
  generations 1 and 2. Both retained one message ID and one business-payload
  SHA-256; the wire/hash changed with the new generation. The replay Ack was
  forwarded and accepted.
- The session converged at generation 2 to the safe fail-closed state
  `RecoveryRequired / CAPABILITY_SNAPSHOT_REQUIRED`. There was no third
  `SessionHello`, third recovery report, or reconnect loop.
- Real OnboardHmi Ack-drop over TLS: INCONCLUSIVE because no supported trusted
  loopback server certificate with an accessible private key was provided.
  No root-trust or certificate-store bypass was attempted.
- No real RIoT order, vehicle movement, fabricated safety eligibility, external
  credential, or Golden WPF run occurred. Business table counts remained zero.
- Secret scan: PASS.

## Classification

The prior Onboard-owned replay blocker is closed by this rerun. The retained
classification remains `formalSlicePass=false`; W2G-IS-00 and W2G-IS-06,
complete G3, and RC remain `INCONCLUSIVE` because the supported trusted
real-Onboard TLS combination and the wider W2G-IS-00 through W2G-IS-07 field
prerequisites were not exercised.

The earlier red and split-transport evidence directories remain unchanged.
