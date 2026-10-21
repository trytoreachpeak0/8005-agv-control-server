# control-server#800：合成 L2 `staging-reservations-not-full` 的红绿证据

本机 2026-10-21，合成装置（假 RIoT、假 MesIngest、合成车载端），槽位 0，经 `Invoke-HeavyLocal.ps1` 跑。每份只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`；完整目录（含日志与库快照）在本机 `C:/work/8005-scratch/cs800/l2/`。

| 目录 | 服务端 | 结论 |
| --- | --- | --- |
| `green/` | `0fd2b4429`（修复）+ 场景脚本 | PASS，4/4 |
| `red/` | 缺陷版本：三个服务端源文件与两个测试文件取自测试提交 `38f552a98`（修复之前），场景脚本同上 | FAIL：L2-SRN-01（0 篮、只被预留占满就 `VEHICLE_FULL`）、L2-SRN-04（锁住之后乙、丙装完车自己开往卸货站，`AwaitingGateArrival CLOSED/VEHICLE_FULL`） |

`red/SUMMARY.md` 身份表里的 `controlServerCommit` 写的是 `9b071be3b`：编排器取的是检出的 HEAD，跑红时那五个文件是用 `git checkout 38f552a98 -- <文件>` 临时换回的，跑完已还原（`git status` 干净）。

另有一次 `green-1` 判 FAIL，是场景脚本自己的错（`@(Invoke-L2Query …).Count` 恒为 1，把 0 篮读成 1 篮），修了脚本之后的 `green-2` 才是这里的 `green/`；`green-1` 的完整目录留在本机同一处。
