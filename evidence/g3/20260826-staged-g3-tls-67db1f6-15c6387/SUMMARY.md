# Staged G3 real-Onboard TLS attempt: runner issuance failure

Result: `INCONCLUSIVE_RUNNER_ERROR`.

This no-movement attempt bound ControlServer
`67db1f66ef520b80876aa17a0bd70524589049b0`, OnboardHmi
`15c6387801fa2154fb69441eac460fea9d0999c5`, slots-simulator
`fb5f7c593742bf98bc3957b8729a38aad5321f28`, and protocol
`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.

Protocol G1 and all three Release publishes completed successfully. The runner
then failed before certificate export or trust-store mutation because PowerShell
could not invoke `CopyWithPrivateKey` as an instance method. No listener,
product process, or temporary trusted root remained after the run. The full
machine-readable result is in `run-result.json`; this attempt is retained as
red evidence and must not be promoted to a staged slice, formal G3, or RC pass.
