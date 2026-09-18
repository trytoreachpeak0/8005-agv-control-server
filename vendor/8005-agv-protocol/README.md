# vendor/8005-agv-protocol

`8005-agv-protocol` 里被本仓库当作契约读取的文件，按字节存一份副本。

## 为什么是副本而不是引用

`integration-slices/index.json` 是切片家族表与向量清单的权威定义，`manifest/release.json` 是
发布身份、63 条消息面与 11 条 denylist 的权威定义，权威副本都在 `8005-agv-protocol`。本仓库的
`ProtocolVectorTestBindingArchitectureTests`、`ProtocolIdentityArchitectureTests`、
`ProtocolMessageSurfaceArchitectureTests` 与 `IntegrationSliceTraitArchitectureTests` 要在 CI 的
headless runner 上把它们当清单来源用，而那里 `actions/checkout` 只取一个仓库——**读兄弟目录在
CI 上不成立**，两个仓库是各自独立的克隆，没有 submodule 也没有包。

所以取用方式是 vendor 一份副本，**并把它的 SHA-256 钉在测试里**。副本被改一个字符，那条测试
立刻红——这正是它不构成「第二份手抄清单」的原因：手抄清单会悄悄漂移，按字节绑定的副本不会。

**三份副本的钉法各不相同，`manifest/release.json` 那份没有新增哈希常量。** `ProtocolCandidateIdentity`
的 `ManifestSha256` 按定义就是这个文件的 SHA-256，服务端每条报文都带着它，所以
`ProtocolIdentityArchitectureTests.TheVendoredManifestIsTheProtocolManifestByteForByte` 直接拿
那个常量去核副本：常量让副本可信，副本让常量可查，两边互为凭据。`index.json` 那份没有这种现成
的凭据，故仍单独钉在 `ProtocolVectorTestBindingArchitectureTests.ApprovedIndexSha256`。

`schemas/` 是 69 个文件，钉的是**一个树摘要**：按相对路径排序，逐个写入「路径 ＋ 换行 ＋ 该文件
SHA-256 的小写十六进制 ＋ 换行」，整体再取一次 SHA-256，常量是
`ProtocolPayloadShapeArchitectureTests.ApprovedSchemaTreeSha256`。**这个摘要是我们自己的，不是
manifest 里的 `schemaBundleSha256`**——后者由协议仓自己的打包算法算出，本仓不复现它；拿它来钉
就变成了重新实现一个算法，而不是核对一份副本。

哈希只钉在测试里一处，本文件不重复它。

同一条纪律的另一处应用是 `vendor/8005-agv-program/`，那里 vendor 的是 RIoT 调用白名单文档。

## 当前副本

两份都取自同一个提交。

| 项 | 值 |
| --- | --- |
| 来源仓库 | `8005-agv-protocol` |
| 来源提交 | `86575456c847041515b7b75e8851a00e0d939804`（tag `protocol-v2.0.0`） |
| 取用日期 | 2026-09-16 |

| 来源路径 | 内容 |
| --- | --- |
| `integration-slices/index.json` | `schemaVersion 2.0.0`、16 条切片（`FP-IS-00`～`15`）、`vectorIds` 条目 36、去重 33 |
| `manifest/release.json` | `status CONTENT_SNAPSHOT`、`releaseVersion 2.0.0`、`protocolVersion 3`、`profileId AGV_FULL_PRODUCT`、63 条消息、11 条 denylist、1785 条文件表项 |
| `schemas/`（整棵树） | 69 个文件，`$id` 段 `agv-full-product/v3` |

那个提交即协议 `protocol-v2.0.0` 的发布内容（`ApprovalStatus APPROVED_RELEASE`）：`8005-agv-program#96` 于
2026-09-16 把它冻结为 `2.0.0` 候选并在其上通过 G1（协议仓 PR 的 CI 与合并顶端的干净克隆各一次），
`8005-agv-program#97` 同日用注释 tag `protocol-v2.0.0` 发布**同一个提交**——manifest 未变，批准只以外置
attestation（Release asset `release-approval.json`）给出。三份文件从该提交的对象里导出（不经协议仓工作树），
逐个与 manifest 自带的文件表 SHA-256 核对一致后整份拷入；发布后服务端没有重新 vendor，因为副本与发布提交
逐字节相同（`8005-agv-control-server#89` 已逐个核对）。

## 上游改了以后怎么刷新

副本与上游之间**没有自动同步**，也不可能有：CI 看不到另一个仓库。上游动了切片表或向量清单，
就要有人跑一遍这四步。

1. 在同时有两个仓库的机器上，把上游文件整份拷过来（动了哪份就拷哪份，两份都动就都拷）：

   ```bash
   cp <8005-agv-protocol>/integration-slices/index.json vendor/8005-agv-protocol/integration-slices/index.json
   cp <8005-agv-protocol>/manifest/release.json vendor/8005-agv-protocol/manifest/release.json
   cp -r <8005-agv-protocol>/schemas/. vendor/8005-agv-protocol/schemas/
   ```

2. 算新哈希：

   ```bash
   sha256sum vendor/8005-agv-protocol/integration-slices/index.json
   sha256sum vendor/8005-agv-protocol/manifest/release.json
   ```

3. `index.json` 的新哈希填进
   `tests/ControlServer.Tests/ProtocolVectorTestBindingArchitectureTests.cs` 的
   `ApprovedIndexSha256`；`release.json` 的新哈希填进
   `src/ControlServer.Domain/ProtocolCandidateIdentity.cs` 的 `ManifestSha256`，**并把那里其余
   八个常量与 `src/ControlServer.Host/appsettings.json` 的 `ProtocolCandidate` 一起改到位**——
   那不是抄哈希，那是换一次协议身份，`ProtocolIdentityArchitectureTests` 会逐字段核对。
   最后把上表的来源提交、取用日期与内容更新。

4. 跑测试。三处会随之报缺或报多，**都不是测试写错了**：

   - **向量清单增删** → `ProtocolVectorTestBindingArchitectureTests`：新向量还没有具名测试，
     或者某条测试还标着一个已被删除的 `vectorId`。
   - **消息面增删** → `ProtocolMessageSurfaceArchitectureTests`：新消息服务端还没实现（钉进
     `MessagesWithoutAnImplementation` 并写明理由），或者某条已实现的消息还留着钉。
   - **切片表增删** → `IntegrationSliceTraitArchitectureTests`：某条测试标着一个已不存在的
     切片 id。
   - **payload 形状改动** → `ProtocolPayloadShapeArchitectureTests`：服务端发出的快照少了一个
     新增的必填字段，或者还带着一个已被删掉的字段，或者某个枚举值已不在表内。

**整份拷贝，不要手工编辑副本。**副本与上游的差异没有任何机制能自动发现，唯一的保障
是「它永远是 `cp` 出来的」这条纪律。

## 行尾

`.gitattributes` 给这个目录挂了 `-text`，禁止行尾转换。哈希是按字节绑定的，
checkout 时把 LF 换成 CRLF 会让摘要漂移。
