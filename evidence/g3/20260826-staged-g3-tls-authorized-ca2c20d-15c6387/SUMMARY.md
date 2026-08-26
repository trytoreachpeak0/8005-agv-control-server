# Authorized staged G3 real-Onboard TLS attempt: chain precheck failure

Result: `INCONCLUSIVE_RUNNER_ERROR`.

This authorized no-movement attempt bound ControlServer
`ca2c20d1530af27ead98fc1dcab2cf39226e886e`, OnboardHmi
`15c6387801fa2154fb69441eac460fea9d0999c5`, slots-simulator
`fb5f7c593742bf98bc3957b8729a38aad5321f28`, and protocol
`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.

Protocol G1 and all three Release publishes completed successfully. With the
user's explicit authorization, Windows installed the uniquely named staged G3
test root into `CurrentUser/Root`. The runner then stopped before starting any
listener because its same-process system-chain precheck returned `PartialChain`.
The exact root deletion prompt was confirmed, and certificate stores, ports,
and product processes were verified clean afterward.

The precheck is runner-owned and is not evidence that the real Onboard TLS
client rejected the installed root. The next runner revision uses
`CustomRootTrust` only for structural certificate validation and leaves the
actual system-trust decision to the real Onboard `SslStream` handshake. This
attempt remains red evidence and is not a staged slice, formal G3, or RC pass.
