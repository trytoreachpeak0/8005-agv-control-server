# vendor/8005-agv-program

`8005-agv-program` 里被本仓库当作契约读取的文档，按字节存一份副本。

## 为什么是副本而不是引用

`docs/riot-call-allowlist.md` 是 RIoT 调用白名单的产品文档，权威副本在
`8005-agv-program`。本仓库的 `RiotCallAllowlistArchitectureTests` 要在 CI 的 headless runner
上把它当清单来源用，而那里 `actions/checkout` 只取一个仓库——**读兄弟目录在 CI 上不成立**，
两个仓库是各自独立的克隆，没有 submodule 也没有包。

所以取用方式是 vendor 一份副本，**并把它的 SHA-256 钉在测试里**
（`RiotCallAllowlistArchitectureTests.ApprovedAllowlistSha256`）。副本被改一个字符，
那条测试立刻红——这正是它不构成「第二份手抄清单」的原因：手抄清单会悄悄漂移，
按字节绑定的副本不会。

哈希只钉在测试里一处，本文件不重复它。

## 当前副本

| 项 | 值 |
| --- | --- |
| 来源仓库 | `8005-agv-program` |
| 来源路径 | `docs/riot-call-allowlist.md` |
| 来源提交 | `3bc055c450c8a1d8d395d7d036042d1c890e383f`（分支 `fp/batch-2`） |
| 取用日期 | 2026-09-08 |

## 上游改了以后怎么刷新

副本与上游之间**没有自动同步**，也不可能有：CI 看不到另一个仓库。上游动了白名单，
就要有人跑一遍这四步。

1. 在同时有两个仓库的机器上，把上游文件整份拷过来：

   ```bash
   cp <8005-agv-program>/docs/riot-call-allowlist.md vendor/8005-agv-program/docs/riot-call-allowlist.md
   ```

2. 算新哈希：

   ```bash
   sha256sum vendor/8005-agv-program/docs/riot-call-allowlist.md
   ```

3. 把 `tests/ControlServer.Tests/RiotCallAllowlistArchitectureTests.cs` 里的
   `ApprovedAllowlistSha256` 换成新值，把上表的来源提交与取用日期一并更新。

4. 跑测试。**清单收紧时，服务端可能有调用越界**——那不是测试写错了，是产品代码要跟着改。

**整份拷贝，不要手工编辑副本。**副本与上游的差异没有任何机制能自动发现，唯一的保障
是「它永远是 `cp` 出来的」这条纪律。

## 行尾

`.gitattributes` 给这个目录挂了 `-text`，禁止行尾转换。哈希是按字节绑定的，
checkout 时把 LF 换成 CRLF 会让摘要漂移。
