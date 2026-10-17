# 生产切换 cs#564 现场证据（2026-10-14 至 10-17）

控制端原件在 `F:/8005-scratch/cutover-ev/production-20261014/`，这里是精简后入库的副本。

- 根目录和 `d2/`：10-14～10-15 换库、装生产段、三车上线。
- `d3/`、`d4/`：10-16～10-17 换装之前的生产运行。
- `d5/`：10-17 v4 换装与开闸后的必测验证。总结在 `d5/SUMMARY.md`。
- `scripts/`：现场用过的只读或受治理脚本副本。

## 与原件不同的地方

- **操作员工号已打码**：入库副本里写成 `S00*****`，原件在控制端，没有改动。
- **未入库**：车载端日志簿副本（`*.db` 和 `-wal`/`-shm`）和行程监看 `d2/34-trip-watch.txt`。它们的 SHA-256 记在 `EXCLUDED-SHA256.txt`。监看文件那一行是 17:28 的快照，之后文件还在增长。
- 两个 0 字节文件（`d2/11-agv03-journey-log.txt`、`scripts/ro-sql-db.ps1`）已删除。
- 其余文件只是行尾由 CRLF 统一成了 LF，内容逐字节一致。

## 编码

大部分文件是 UTF-8。下面 14 个文件是 GBK 编码（ssh 输出时控制台的代码页），没有转码，查看时请按 GBK 打开：

- `11-step19-whatif.txt`
- `13-step19-deploy-s1.txt`
- `30-s3-whatif.txt`
- `31-s3-deploy.txt`
- `d2/41-agv01-resync.txt`
- `d2/56-c11-cancel-forensics-summary.txt`
- `d2/57-agv02-recovery-state.txt`
- `d2/62-cs723-0740-outbox-summary.txt`
- `d4/02-start-agv01.txt`
- `d5/01-v4-whatif.txt`
- `d5/03-v4-deploy.txt`
- `d5/11-v4-deploy-s1.txt`
- `d5/19-s3-whatif.txt`
- `d5/20-s3-deploy.txt`
