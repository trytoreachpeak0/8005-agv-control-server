# 8005-agv-protocol `protocol-v0.3.0` 的 vendor 副本

这里放的是 `8005-agv-protocol` 在 tag `protocol-v0.3.0`（commit `345c53c58517968192c87c3e7777ed08ddb48726`）上的两样东西，逐字节原样：

| 路径 | 内容 | 自验 |
| --- | --- | --- |
| `manifest/release.json` | 发布 manifest | 文件 SHA-256 必须等于 `ProtocolCandidateIdentity.ManifestSha256`（`b6c81ca9…`） |
| `schemas/` | 60 份 JSON Schema | 按协议仓 `tools/finalize-manifest.mjs` 的定义重算，必须等于 `ProtocolCandidateIdentity.SchemaBundleSha256`（`68bfd531…`） |

用它的只有 `tools/ControlServer.SchemaConformance`：测试跑完之后，它把本次测试里服务端发出的每一条报文按 `messageType` 选定唯一那份 message schema 逐条验（#33）。两个哈希在每次运行开头自验，对不上就退出码 2，一条报文都不验——**那两个常量此前只跟另一份自己比过，这是它们第一次被拿去验内容。**

## 为什么不能改这里的文件

- 改一个字节，两个哈希就对不上，校验器拒绝运行。这是有意的：契约是 schema 与那两个 SHA-256，不是这份拷贝。
- `.gitattributes` 对 `vendor/8005-agv-protocol/**` 设了 `-text`，入库与检出都不做换行转换。去掉它，Windows 上的检出可能就不再是 tag 里的字节。

## 协议升版时

1. 用 `git -C <协议仓> archive <新 tag> schemas manifest/release.json | tar -x -C vendor/8005-agv-protocol/<新 tag>` 放一份新的，删掉旧目录。
2. `ProtocolCandidateIdentity` 的 pin 跟着改。校验器按 `ProtocolCandidateIdentity.Tag` 找目录，只改 pin 不换 vendor 会直接报「找不到」，不会拿旧 schema 去验新报文。
