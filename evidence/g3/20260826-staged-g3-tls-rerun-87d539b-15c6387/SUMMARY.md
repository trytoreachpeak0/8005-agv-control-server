# Staged G3 real-Onboard TLS rerun: trust authorization boundary

Result: `INCONCLUSIVE_TRUST_AUTHORIZATION_REQUIRED`.

This no-movement rerun bound ControlServer
`87d539b3ed7dd01a23531437f4be5ddb0e2ac9e6`, OnboardHmi
`15c6387801fa2154fb69441eac460fea9d0999c5`, slots-simulator
`fb5f7c593742bf98bc3957b8729a38aad5321f28`, and protocol
`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.

Protocol G1 and all three Release publishes completed successfully. Windows
then displayed its Security Warning before adding the unique test root to
`CurrentUser/Root`. The run was deliberately stopped because no user approval
for that system trust change was present. No root certificate was installed;
the staged certificate stores, ports, and product processes were verified
clean afterward. This interrupted evidence has no `run-result.json` and must
not be promoted to a staged slice, formal G3, or RC pass.
