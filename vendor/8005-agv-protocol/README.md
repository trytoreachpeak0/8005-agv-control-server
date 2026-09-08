# vendor/8005-agv-protocol

`8005-agv-protocol` 里被本仓库当作契约读取的文件，按字节存一份副本。

## 为什么是副本而不是引用

`integration-slices/index.json` 是切片家族表与向量清单的权威定义，权威副本在
`8005-agv-protocol`。本仓库的 `ProtocolVectorTestBindingArchitectureTests` 要在 CI 的
headless runner 上把它当清单来源用，而那里 `actions/checkout` 只取一个仓库——**读兄弟目录在
CI 上不成立**，两个仓库是各自独立的克隆，没有 submodule 也没有包。

所以取用方式是 vendor 一份副本，**并把它的 SHA-256 钉在测试里**
（`ProtocolVectorTestBindingArchitectureTests.ApprovedIndexSha256`）。副本被改一个字符，
那条测试立刻红——这正是它不构成「第二份手抄清单」的原因：手抄清单会悄悄漂移，
按字节绑定的副本不会。

哈希只钉在测试里一处，本文件不重复它。

同一条纪律的另一处应用是 `vendor/8005-agv-program/`，那里 vendor 的是 RIoT 调用白名单文档。

## 当前副本

| 项 | 值 |
| --- | --- |
| 来源仓库 | `8005-agv-protocol` |
| 来源路径 | `integration-slices/index.json` |
| 来源提交 | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b`（分支 `fp/v2-candidate`） |
| 取用日期 | 2026-09-08 |
| 内容 | `schemaVersion 2.0.0`、16 条切片（`FP-IS-00`～`15`）、`vectorIds` 条目 34、去重 31 |

那个提交即协议 v2 候选，G1 于 2026-09-08 在协议仓 self-hosted runner 上实跑通过。

## 上游改了以后怎么刷新

副本与上游之间**没有自动同步**，也不可能有：CI 看不到另一个仓库。上游动了切片表或向量清单，
就要有人跑一遍这四步。

1. 在同时有两个仓库的机器上，把上游文件整份拷过来：

   ```bash
   cp <8005-agv-protocol>/integration-slices/index.json vendor/8005-agv-protocol/integration-slices/index.json
   ```

2. 算新哈希：

   ```bash
   sha256sum vendor/8005-agv-protocol/integration-slices/index.json
   ```

3. 把 `tests/ControlServer.Tests/ProtocolVectorTestBindingArchitectureTests.cs` 里的
   `ApprovedIndexSha256` 换成新值，把上表的来源提交、取用日期与内容一并更新。

4. 跑测试。**向量清单增删时，绑定会随之报缺或报多**——那不是测试写错了，是新向量还没有具名
   测试，或者某条测试还标着一个已被删除的 `vectorId`。

**整份拷贝，不要手工编辑副本。**副本与上游的差异没有任何机制能自动发现，唯一的保障
是「它永远是 `cp` 出来的」这条纪律。

## 行尾

`.gitattributes` 给这个目录挂了 `-text`，禁止行尾转换。哈希是按字节绑定的，
checkout 时把 LF 换成 CRLF 会让摘要漂移。
