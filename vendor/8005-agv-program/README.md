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
| 来源提交 | `01c87efe26465875d658cd2ae90133ac1d4e8faf`（`main`，发布需求基线 v1.9.0：1.2、1.3 节随 CP-0010 跟改，control-server#406） |
| 取用日期 | 2026-10-01 |

## 上游改了以后怎么刷新

副本与上游之间**没有自动同步**，也不可能有：CI 看不到另一个仓库。上游动了白名单，
就要有人跑一遍这四步。

1. 在同时有两个仓库的机器上，从上游**提交里的 git blob** 整份取出来，不要从工作树拷：

   ```bash
   git -C <8005-agv-program> show <来源提交>:docs/riot-call-allowlist.md > vendor/8005-agv-program/docs/riot-call-allowlist.md
   ```

   program 仓开着 `core.autocrlf=true`，工作树里那份是 CRLF，而仓库里存的是 LF。用 `cp` 拷工作树会得到一份
   CRLF 副本和一个对不上 program 的哈希，测试照样绿，但副本已经不是被批准的那份字节（CP-0010 第四节第 3 条）。

2. 算新哈希：

   ```bash
   sha256sum vendor/8005-agv-program/docs/riot-call-allowlist.md
   ```

3. 把 `tests/ControlServer.Tests/RiotCallAllowlistArchitectureTests.cs` 里的
   `ApprovedAllowlistSha256` 换成新值，把上表的来源提交与取用日期一并更新。

4. 跑测试。**清单收紧时，服务端可能有调用越界**——那不是测试写错了，是产品代码要跟着改。

**整份拷贝，不要手工编辑副本。**副本与上游的差异没有任何机制能自动发现，唯一的保障
是「它永远是从上游提交 `git show` 出来的」这条纪律。

## 行尾

`.gitattributes` 给这个目录挂了 `-text`，禁止行尾转换。哈希是按字节绑定的，
checkout 时把 LF 换成 CRLF 会让摘要漂移。
