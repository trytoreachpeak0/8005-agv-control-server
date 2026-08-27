# Local ControlServer Windows Service deployment

Date: 2026-08-27  
Repository: `https://github.com/trytoreachpeak0/8005-agv-control-server`  
Branch: `ControlServer_MVP`  
Product/source commit: `1f14e7436423c6dae5c0dc1fdfabc0cfbb7c3799`  
Formal result: `LOCAL_DEPLOYMENT_PASS` — the local Windows Service deployment is healthy; formal W2G-IS-00 through W2G-IS-07 G3 and RC remain `INCONCLUSIVE`.

## Authorized local deployment boundary

- First-stage target is the current developer machine.
- ControlServer and Onboard are co-located for the test stage.
- The Onboard-facing safety endpoint is `https://localhost:58007`; the dedicated leaf certificate contains only the `localhost` SAN.
- The service identity is `LocalSystem`.
- A dedicated development root may be installed in `CurrentUser/Root` and must be removed or replaced when ControlServer moves to its final machine.
- The existing User-scope `CONTROL_SERVER_RIOT_CALL_API_KEY` may be copied to Machine scope during the approved installation without printing, hashing, logging, or committing its value. The User-scope value remains unchanged.
- `JourneyRuntime` remains disabled. The deployment does not authorize RIoT mutation, order creation, vehicle movement, or formal G3.

## Published deployment assets

- `scripts/Publish-ControlServer.ps1` produces a clean-directory, self-contained `win-x64` package with a source commit and per-file SHA-256 manifest.
- `scripts/Install-ControlServerLocal.ps1` requires explicit root-trust and User-to-Machine secret-copy switches, rejects replacement of an existing service/install directory, backs up the data root, generates the dedicated root and leaf certificate, restricts installation/data/PFX/service-registry ACLs, installs an automatic `LocalSystem` service, and performs Schannel HTTPS live checks across initial start, stop/start, and restart.
- Rollback continues through independent cleanup steps and reports cleanup errors instead of stopping at the first cleanup failure.
- The private development CA has no CRL endpoint. Schannel probes use `--ssl-revoke-best-effort`: chain, trusted root, SAN, validity, and any available revocation evidence remain enforced; `--insecure` is never used.

The current package is bound to source commit `1f14e7436423c6dae5c0dc1fdfabc0cfbb7c3799`; its `deployment-manifest.json` SHA-256 is
`0895f279e9a6a17e81725ed26f1037eb4b20f79a7706a03a3e874ec88a851cb0`.

## Defect found and fixed

The first real Kestrel HTTPS handshakes failed while the Host remained running. A controlled foreground reproduction with a temporary SQLite database and `JourneyRuntime.enabled=false` captured the server exception:

`Authentication failed because the platform does not support ephemeral keys.`

The HTTPS projection loaded its PFX with `MachineKeySet | EphemeralKeySet`. Windows Schannel cannot acquire server credentials from that ephemeral private key. The NDJSON TLS path already used `OnboardTlsCertificateLoader.Load`, which loads the PFX with `MachineKeySet` and has a real Schannel handshake regression test. Commit `1f14e7436423c6dae5c0dc1fdfabc0cfbb7c3799` makes the Kestrel HTTPS path reuse the same loader. No TLS validation bypass was added.

Validation after the product change:

- Focused `OnboardTlsCertificateLoaderTests` run: 1 passed, 0 failed, 0 skipped.
- `dotnet format ControlServer.sln --verify-no-changes --no-restore`: PASS.
- Release non-incremental solution build: 0 warnings, 0 errors.
- Full owner test project: 101 passed, 0 failed, 0 skipped.

## Installation result

All pre-success installer failures executed rollback; the hardened rollback reported zero cleanup errors. Two direct UAC launches were cancelled before the installer began. An explicitly authorized, highest-run-level, interactive-user one-shot scheduled task then ran the exact installer and deleted itself after completion.

The first one-shot run exposed Schannel `CRYPT_E_NO_REVOCATION_CHECK` for the offline private CA and rolled back with zero cleanup errors. Installer commit `160a6c86ef1098927926a86ee05cf7e595eb447e` changed only the health/version probe to best-effort revocation as described above. The second one-shot run returned task result `0` and produced deployment result `PASS`.

Deployment result identity:

- Result JSON SHA-256: `c321f7324727edb01ef7abd84b125daca02a86fe195fafa5ca763c0c9d7cc6c8`.
- Configuration SHA-256: `da3d2fd90f6a4f5f097fd4cd8c7d2d3b772d5a130aae59a8de342eece6679734`.
- Leaf thumbprint: `8AFB06F14C65D438A0D8CD139FE82026278D8167`, SAN `localhost` only.
- Trusted development root thumbprint: `8CEF9E6B09A7202AB6B8DF5323E6A48E3CE47003`, store `CurrentUser/Root`.
- Backup path: `C:\ProgramData\8005\ControlServer-backups\20260827T075424Z`.

Independent post-install readback confirmed:

- Windows Service `8005 AGV ControlServer` is `Running`, `Auto`, under `LocalSystem`.
- Port 58005 has one loopback TLS listener owned by the service; HTTPS port 58007 listeners are owned by the same service.
- `https://localhost:58007/health/live` returns `{"status":"live"}` through Schannel with normal chain/name validation and best-effort offline revocation.
- `/version` returns approved `protocol-v0.1.1` and exact protocol commit `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`.
- User- and Machine-scope RIoT credential values are present and byte-for-byte equal; no value or hash was printed. Machine-scope Onboard credential and certificate password are present. Service-specific environment is present under the restricted service registry ACL.
- The exact development root is present once. The one-shot scheduled task is absent after completion.
- Non-elevated inspection is denied access to the protected SQLite file, as required by the installed ACL. The elevated installer completed all nine migrations before its three lifecycle HTTPS checks.
- `JourneyRuntime.enabled=false`; no RIoT mutation, order creation, vehicle movement, or safety-projection field call occurred.

This closes only the local install/start/stop/restart and HTTPS trust prerequisite. Formal G3 still waits for Wang Kun's protected Onboard HTTPS provider commit, secure Onboard credential delivery, an authorized no-movement field preflight, and separately authorized movement qualification.
