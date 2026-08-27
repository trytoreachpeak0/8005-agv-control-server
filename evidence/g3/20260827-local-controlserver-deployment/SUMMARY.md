# Local ControlServer deployment preparation and Schannel blocker

Date: 2026-08-27  
Repository: `https://github.com/trytoreachpeak0/8005-agv-control-server`  
Branch: `ControlServer_MVP`  
Product/source commit: `1f14e7436423c6dae5c0dc1fdfabc0cfbb7c3799`  
Formal result: `INCONCLUSIVE` — the product fix and package are ready, but the elevated Windows Service installation has not run.

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

## Installation status and cleanup

All installer failures before the product fix executed rollback. The hardened final rollback reported zero cleanup errors. A new package was generated after the fix, but two attempts to launch the elevated installer were cancelled at the Windows UAC prompt before the installer process began.

Post-cancellation verification:

- ControlServer Windows Service absent.
- Ports 58005 and 58007 have no listeners.
- `C:\Program Files\8005 AGV\ControlServer` absent.
- No matching dedicated development root remains in `CurrentUser/Root`.
- Machine-scope `CONTROL_SERVER_RIOT_CALL_API_KEY` absent.
- Machine-scope `CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD` absent.
- Existing User-scope RIoT secret and Machine-scope Onboard credential were not disclosed or changed.

The next action is to run the prepared installer from an administrator context. Until its result JSON reports PASS and the final post-install state is independently re-read, local deployment remains `INCONCLUSIVE`.
