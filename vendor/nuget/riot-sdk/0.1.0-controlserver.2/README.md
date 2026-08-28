# RIoT SDK 0.1.0-controlserver.2

Immutable local feed for ControlServer.

- Source repository: `https://github.com/trytoreachpeak0/8005---AGV`
- Source branch: `codex/riot-sdk-controlserver-integration`
- Exact source commit: `e708f874fa3b76f9ed1cf39c2f97e4a026c13c10`
- Package version: `0.1.0-controlserver.2`

The packages were produced from a clean sparse clone at the exact commit:

```powershell
dotnet pack .\csharp\RIoT.Sdk.sln -c Release `
  -p:RepositoryCommit=e708f874fa3b76f9ed1cf39c2f97e4a026c13c10 `
  -o <immutable-feed>
```

All three `.nuspec` files were inspected after packing. Their repository URL,
repository commit, version, and package dependencies match this record. File
digests are in `SHA256SUMS`; NuGet lock-file `contentHash` values provide the
consumer-side integrity gate.

SDK validation before packing:

- C#: 76 passed, 0 failed, 0 skipped.
- Python: 73 passed, 0 failed, 1 environment-dependent smoke test skipped.
- C# format verification passed.
- Focused pseudo-mutation audit: 11 of 11 safety mutations killed before the
  nullable-numeric regression; the new paired regression passed in both languages.

Version `.2` supersedes `.1` after ControlServer integration exposed that a
JSON `null` optional numeric fact was being parsed as an internal type error.
The `.1` packages were not used for the final ControlServer lock files.

Do not replace a package while retaining this version. Publish a new immutable
version and regenerate the ControlServer lock files instead.
