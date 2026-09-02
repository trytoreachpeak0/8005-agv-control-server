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

---

## 2026-09-02 补记：上面的来源信息已经无法直接解析

`8005---AGV` 在 2026-09-02 被拆分并改名，历史同时被重写（清掉 `.artifacts/`，
914 MB → 52 MB）。后果有两条：

- **仓库名**：`8005---AGV` 现在是 `8005-agv-program`，GitHub 旧 URL 会自动重定向。
  但 RIoT SDK 的源码已经不在那个仓库里了，它去了
  [`riot-sdk`](https://github.com/trytoreachpeak0/riot-sdk)（原
  `rcs/riot-sdk/` 提到仓库根）。
- **commit `e708f874fa3b76f9ed1cf39c2f97e4a026c13c10` 已不存在**。历史重写换掉了
  全部 commit hash，这个引用解析不出来。

拆分前的完整 mirror 备份在控制端 `C:/Users/szy/8005-restructure-backup/8005---AGV-backup.git`，
需要核对这个包的原始来源时从那里查。

**上面的原始记录保持不动**——它记载的是打包当时的事实。
