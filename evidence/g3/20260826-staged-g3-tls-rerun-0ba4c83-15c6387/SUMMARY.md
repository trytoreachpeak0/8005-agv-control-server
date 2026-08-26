# Real-Onboard TLS recovery replay passed; wrapper cleanup failed

Result: `TLS_VECTOR_PASS_WRAPPER_CLEANUP_ERROR`.

This authorized no-movement run bound ControlServer
`0ba4c83119e2388285857703f43e0c45a5410e04`, OnboardHmi
`15c6387801fa2154fb69441eac460fea9d0999c5`, slots-simulator
`fb5f7c593742bf98bc3957b8729a38aad5321f28`, and protocol
`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.

The real Onboard connected through the TLS fault proxy to the TLS
ControlServer. Generation 1 sent a `RecoveryStateReport`; its first
`DurableAck` was dropped and the connection was closed. Generation 2 then
replayed the same message ID and business-payload SHA-256 over a new TLS
connection, received the forwarded Ack, and converged to the expected
fail-closed `RecoveryRequired` state. Protocol G1, TLS identity probes, the
three Release publishes, and the no-movement boundary also completed.

The wrapper failed after the product processes stopped because unsuppressed
.NET collection `Add` return values made the PowerShell TLS material result an
array, so assigning `TrustCleanupVerified` raised an error. The exact root
deletion prompt was confirmed; the root, PFX, listeners, and product processes
were verified absent. Because no final `run-result.json` was written, this
directory is retained as red wrapper evidence and is not itself promoted to a
formal staged result, G3 slice, or RC pass.
