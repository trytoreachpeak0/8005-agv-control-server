# 批次 8 与协议 v3 出口的部署包（control-server#393 第 10 步）

只打包，不部署。包本身（约 1300 个文件的二进制）不入库，这里只放清单、哈希、依赖与密钥扫描结果、构建日志。

| 项 | 值 |
| --- | --- |
| 打包所用的服务端提交 | `86045ccf67f129634e5cfdff8310006341e9daa8`（出口最终提交 X，干净的 detached worktree `C:/w2g/x393`） |
| 车载端 | `b9e67a538ba4cdf1916d201a08af40dd28270d14`，克隆 `w2g/fp-v2-impl` 后检出这个提交 |
| 协议 | `protocol-v3.0.0` → `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`，`APPROVED_RELEASE` |
| 命令 | `pwsh -File scripts/New-WireToGateReleaseCandidate.ps1 -OutputRoot C:/w2g/cs393/rc-final -OnboardCommit b9e67a53… -OnboardBranch w2g/fp-v2-impl`，退出码 0 |
| 密钥扫描 | 0 项；钥匙材料文件 0 个（`inventory/secret-scan.json`） |
| 扫描门禁 | PASS；白名单内许可未定的三个包：`riot.sdk.core`、`riot.sdk.facade`、`riot.sdk.generated` |
| 包内文件校验 | `SHA256SUMS.txt` 列出 1345 个文件，逐个校验 1345 个 OK（`sha256sums-verify.txt`） |

**为什么车载端是 `b9e67a53`，不是 `w2g/fp-v2-impl` 现在的顶端 `ca89ef8f`**：G3、真装置、车载端 G2 全部绑在 `b9e67a53` 上。`ca89ef8f` 是车载端证据 PR onboard-hmi#288 的合并提交，相对 `b9e67a53` 只加了 `evidence/`（210 个文件）。用顶端打包，包里的车载端提交就和全部门禁证据对不上了。

## 哈希

打包脚本输出的是包里文件的原始字节（CRLF 换行）的哈希：

| 文件 | 包里的原始字节 | 本目录入库副本（仓库把文本统一为 LF） |
| --- | --- | --- |
| `release-manifest.json` | `dab6d974c6e4c291a2cd1951fb7fd24c5522f5a6ff79d102c2895140bb47e412` | `5a536759786a3862cf5edbc802b18ef6e3e50bef8c99ba77417ea91453090004` |
| `SHA256SUMS.txt` | `21fc6b719d52f26d389643e11bcd789bdb15a688e683292c48a3843aca04d206` | `a41c419a87d8403585e027fe4e738755a016c1ab44c3792d336aac71311934dc` |

右列是去掉 `\r` 之后的哈希。仓库 `.gitattributes` 对这些文件是 `text=auto eol=lf`，所以入库副本只能和右列对上；要和打包时的原件对，用左列。`sha256sum -c` 校验 `SHA256SUMS.txt` 之前要先去掉 `\r`，否则每一行都会因为文件名末尾多一个 `\r` 而报找不到。

## 本机位置

完整的包在本机 `C:\w2g\cs393\rc-final\`，用于审查与核对，保留到出口 PR 合入为止。之前在 `563242d0` 上打的那一次只是过程记录，见 `../20261009-batch-8-exit-563242d0-process-record/`。
