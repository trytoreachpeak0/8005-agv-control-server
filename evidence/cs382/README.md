# control-server#382：WirePin 八份基线重录的判据

协议 v3.0.0 候选把 `CurrentStopWorklistSnapshot.stopEndedReason` 与 `PreDepartureSafetyCheck.checkPurpose` 定为必填，
`tests/ControlServer.Tests/WirePins/` 八份基线因此变了。这里放证明「只变了这两个键」的脚本与输出。

| 文件 | 内容 |
| --- | --- |
| `wirepin-diff.py` | 比对脚本：旧基线取批次分支 `batch-p3/v3@f55669db` 上的 pin，新基线取本票录出的 `.actual` |
| `wirepin-diff.out` | 八份全部 `PASS`：去掉两个新键后载荷逐字相同；新键次数等于旧基线发件箱里两类报文的行数 |
| `wirepin-diff-selftest.out` | 脚本自检：载荷改一个无关字符、表头改一个修订号、删掉一个新键，三种都报 `FAIL` |

复现：

```bash
mkdir -p /tmp/pinbase
for f in $(git ls-tree --name-only f55669db tests/ControlServer.Tests/WirePins/); do git show f55669db:$f > /tmp/pinbase/$(basename $f); done
python evidence/cs382/wirepin-diff.py /tmp/pinbase tests/ControlServer.Tests/WirePins
```

脚本第一版把「线上」一节的行也数进应有次数，八份全报次数不符；那一节只有摘要、没有载荷，新键不可能出现在那里。改成只数发件箱行后八份对上。
取值与 `8005-agv-program` PR #163 正文第 3 项的对照表一致。
