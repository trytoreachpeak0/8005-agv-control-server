# vendor/8005-agv-protocol

`8005-agv-protocol` 里被本仓库当作契约读取的文件，按字节存一份副本。

## 为什么是副本而不是引用

`integration-slices/index.json` 是切片家族表与向量清单的权威定义，`manifest/release.json` 是
发布身份、65 条消息面与 11 条 denylist 的权威定义，权威副本都在 `8005-agv-protocol`。本仓库的
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

`schemas/` 是 71 个文件，钉的是**一个树摘要**：按相对路径排序，逐个写入「路径 ＋ 换行 ＋ 该文件
SHA-256 的小写十六进制 ＋ 换行」，整体再取一次 SHA-256，常量是
`ProtocolPayloadShapeArchitectureTests.ApprovedSchemaTreeSha256`。**这个摘要是我们自己的，不是
manifest 里的 `schemaBundleSha256`**——后者由协议仓自己的打包算法算出，本仓不复现它；拿它来钉
就变成了重新实现一个算法，而不是核对一份副本。

哈希只钉在测试里一处，本文件不重复它。

同一条纪律的另一处应用是 `vendor/8005-agv-program/`，那里 vendor 的是 RIoT 调用白名单文档。

## 当前副本

三份都取自同一个提交。

| 项 | 值 |
| --- | --- |
| 来源仓库 | `8005-agv-protocol` |
| 来源提交 | `18d4fb3495033624cab412a7ee784f675dfe4cc2`（protocol#25 为 control-server#767 合入 `fp/v2-candidate` 的合并提交，树与候选 `ede2060f` 逐字节相同；无 tag） |
| 取用日期 | 2026-10-18 |

| 来源路径 | 内容 |
| --- | --- |
| `integration-slices/index.json` | `schemaVersion 2.0.0`、16 条切片（`FP-IS-00`～`15`）、`vectorIds` 条目 54、去重 51 |
| `manifest/release.json` | `status CONTENT_SNAPSHOT`、`releaseVersion 5.0.0`、`protocolVersion 6`、`profileId AGV_FULL_PRODUCT`、67 条消息、11 条 denylist |
| `schemas/`（整棵树） | 73 个文件，`$id` 段 `agv-full-product/v6` |

2026-10-18 由 `8005-agv-control-server#767` 刷新到 `18d4fb34`（protocol#25 的合并提交，内容提交 `ede2060f`；与车载端同包）：v5 出口前的第三次修订，只改协议仓的 `docs/wire-notes.md`
（剩余计划的规则对所有计划成立，唯一例外是清桩途中保留已完成的充电桩腿；出发口径 (4) 允许服务端从严；补出处 CP-0015 / REQ-0196），wire-notes 不在副本里，
所以三份里只有 `manifest/release.json` 变了（文件表里 wire-notes 一行与 `fileTableSha256`），manifest SHA-256 为 `dd973918…`；
`index.json` 与 `schemas/` 逐字节相同，`vectorsSha256 291311b2…` 不变。manifest 用 `git show` 从该提交的对象导出。

再早一份是 `13eca82a`。2026-10-17 由 `8005-agv-control-server#748` 刷新到 `13eca82a`（与车载端 `8005-agv-onboard-hmi#362` 同包）：protocol#23 是 5.0.0 候选的第二次修订，
schema、消息与错误码不变，`schemas/` 树逐字节相同。`FP-IS-02` 加一条向量 `CV-CURRENT-STOP-DEPARTURE-STALE-REVISION-ACCEPTED`（机台取货停靠的出发请求，
清单号过期但无实质差别时受理，服务端行为由 `8005-agv-control-server#742` 实现），旧向量 `CV-CURRENT-STOP-DEPARTURE-STALE-REVISION` 限定为有实质差别；
`CV-MULTI-STOP-PLAN-NINE-LEGS` 的两个结果名改为 `UP_TO_NINE_REMAINING_LEGS_PLANNED`、`DISPLAY_REMAINING_JOURNEY_PLAN`。`index.json` 与
`manifest/release.json` 变了（`vectorsSha256 291311b2…`），manifest SHA-256 为 `128daae5…`，与调度转来的值一致。两份文件用 `git archive` 从合并提交的对象导出，
`index.json` 与 manifest 文件表里的 SHA-256 核对一致，`schemas/` 与上一份逐个比对无差异。

再早一份是 `4e9f3df8`。2026-10-16 由 `8005-agv-control-server#737` 刷新到 `9d791578`，10-17 protocol#22 合入后改钉合并提交 `4e9f3df8`（树相同，manifest 仍是 `743b886d…`，照 v4 钉合并提交 `3fb214e5` 的先例；`ApprovalStatus SUPERSEDING_CANDIDATE`，tag `protocol-v5.0.0` 尚不存在）：protocol#21 在闭合枚举
`TransportTaskType` 末尾追加 `WIRE_TO_FULL_INSPECTION`（焊线/键合机台送全检区），其余只换身份；闭合枚举追加值是破坏性改动，所以升大版本，
两端必须与车载端 `8005-agv-onboard-hmi#360` 同包换装。错误码表、向量（`vectorsSha256 24cc3a5e…`）与 `index.json` 不变。三份文件从该提交的
对象里用 `git archive` 导出（不经协议仓工作树，避免行尾转换），逐个与 manifest 自带的文件表 SHA-256 核对一致
（73 个 schema 与 `index.json`，74 项全部命中）后整份拷入，manifest SHA-256 为 `743b886d…`，与调度转来的值一致。在它上面跑出的 G2／L2 证据一律是 `UNRELEASED_CANDIDATE`。

再早一份是 `3fb214e5`（tag `protocol-v4.0.0`，10-15 取用）：2026-10-16 用注释 tag
`protocol-v4.0.0`（tag 对象 `daeebc5c`，解引用到 `3fb214e5`）发布**同一个提交**——manifest 与候选逐字节相同，批准只以
外置 attestation（Release asset `release-approval.json`，SHA-256 `68f3516e…`）给出，追踪票 `8005-agv-program#184`；
发布后服务端没有重新 vendor，只由 `8005-agv-control-server#735` 改批准状态。内容上，protocol#14 加了
`CurrentStopDepartureRequested`／`CurrentStopDepartureResult` 与 `OPERATOR_DEPARTED`、`CHARGING_REQUIRED` 两个结束原因，
protocol#15 把待送点缺电改成「先送货再充电」，G1 通过。服务端绑定它的是 `8005-agv-control-server#713`，与车载端
`8005-agv-onboard-hmi#349` 背靠背合入。它还是候选时在它上面跑出的 G2／L2 证据一律是 `UNRELEASED_CANDIDATE`。三份文件从该提交的
对象里用 `git archive` 导出（不经协议仓工作树，避免行尾转换），逐个与 manifest 自带的文件表 SHA-256 核对一致
（73 个 schema 与 `index.json`，74 项全部命中）后整份拷入。

2026-10-15 由 `8005-agv-control-server#727` 刷新到 `3fb214e5`：protocol#17、protocol#19 按 CP-0014 改了 4.0.0 候选——机台取货装完可出发、待送点装上一篮后有持货期限（`FP-IS-02` 加五条向量，`schemas/` 与错误码表未变，`index.json` 与 `manifest/release.json` 变了）。同样用 `git archive` 从提交对象导出，manifest SHA-256 为 `beca46a7…`，与调度转来的值一致。待送点两条向量由本票绑定；机台出发三条登记给 `#725`（`VectorsAwaitingTheirSlice`）。

更早一份是 `4d67655c`（10-15 取用，`#713` 绑定）。再早一份是 `protocol-v3.0.0`（`3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`，2026-09-30 取用，10-09 发布）。

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
