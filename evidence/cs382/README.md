# control-server#382：WirePin 八份基线重录的判据

协议 v3.0.0 候选把 `CurrentStopWorklistSnapshot.stopEndedReason` 与 `PreDepartureSafetyCheck.checkPurpose` 定为必填，
`tests/ControlServer.Tests/WirePins/` 八份基线因此变了。这里放证明「只变了这两个键」的脚本与输出。

| 文件 | 内容 |
| --- | --- |
| `wirepin-diff.py` | 比对脚本：旧基线取批次分支 `batch-p3/v3@f55669db` 上的 pin，新基线取本票录出的 `.actual` |
| `wirepin-diff.out` | 八份全部 `PASS`：去掉两个新键后载荷逐字相同；新键次数等于旧基线发件箱里两类报文的行数 |
| `wirepin-diff.out` 之外的取值断言 | 脚本里 `ENDING` 表按每份钉子记录的旅程怎么结束写出应有的 `stopEndedReason`（按 `8005-agv-program` PR #163 第 3 项对照），逐条断言：有项的清单必须为 `null`，空清单必须等于这一份的应有值，出发检查必须是 `DEPARTURE` |
| `wirepin-diff-selftest.out` | 脚本自检六种注入全报 `FAIL`：载荷改一个无关字符、表头改一个修订号、删掉一个新键；空清单 `COMPLETED` 改 `LOAD_CANCELLED`、有项清单写上 `COMPLETED`、`DEPARTURE` 改 `HOLD_RELEASE`。后三种只动新键取值，逐字比对看不见，报错来自取值断言 |

复现：

```bash
mkdir -p /tmp/pinbase
for f in $(git ls-tree --name-only f55669db tests/ControlServer.Tests/WirePins/); do git show f55669db:$f > /tmp/pinbase/$(basename $f); done
python evidence/cs382/wirepin-diff.py /tmp/pinbase tests/ControlServer.Tests/WirePins
```

脚本第一版把「线上」一节的行也数进应有次数，八份全报次数不符；那一节只有摘要、没有载荷，新键不可能出现在那里。改成只数发件箱行后八份对上。
取值由脚本断言（见上表），对照出处是 `8005-agv-program` PR #163 正文第 3 项。第一版只打印取值、不断言，审查实测把 `COMPLETED` 改成 `LOAD_CANCELLED` 仍 PASS，所以补了 `ENDING` 表。
