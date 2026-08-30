# Install rollback under a real failure (`4c13ea0`)

This directory keeps a **failed** isolated install run, not a passing one. It is retained because
it is the only evidence that the install script's rollback path executes under a genuine failure
rather than a simulated one.

The run stopped at `Import-Certificate` into `CurrentUser\Root`, which raises a Windows trust
dialog and therefore fails with `UI is not allowed in this operation` in a non-interactive session.
`install-diagnostic.log` records the sequence: `preflight-complete`, `backup-complete`,
`package-copy-complete`, `certificate-generation-complete`, `failure: ...`,
`rollback-complete errors=0`.

Independently verified immediately after the failure, outside the script:

| Assertion | Observed |
| --- | --- |
| service `8005 AGV ControlServer RC Verify` exists | `False` |
| `C:\Program Files\8005 AGV\ControlServer-RC-Verify` exists | `False` |
| `C:\ProgramData\8005\ControlServer-RC-Verify` exists | `False` |
| production service `8005 AGV ControlServer` status | `Running` |

No result JSON exists for this run: the script writes one only on success.

`a5698da` removed the trust-store import from the default path, so this failure mode no longer
occurs; the passing run is recorded separately.
