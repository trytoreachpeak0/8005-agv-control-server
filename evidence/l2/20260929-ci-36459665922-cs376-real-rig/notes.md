# control-server#376 验收：CI 真装置 run 36459665922

`l2.yml` 的 `real-rig` 作业（`-f rig=real -f mode=consecutive-all`），跑 `real-onboard-cancelled-rebuild-cargo-proof` 与 `real-onboard-in-transit-door-facts`。持有由 Coordinator 8 于 2026-09-28 批准。

`consecutive-all` 的含义是每个场景至少 3 遍（`l2.yml`：`[Math]::Max(3, $scenario.Runs)`），所以实际是 2 个场景各 3 遍，共 6 次。申请时我写成了「各 1 遍」，那是我对这个模式记错了。

## 四行核对

1. artifact `real-rig-evidence` 2089559 字节，未过期，已下载核过。
2. 三行提交取自场景那一步（`Run real-onboard L2 scenarios`）的实际输出，不是 checkout 那一步，与申请清单一致：

   ```
   control-server @ 5ee337fd4fbfb6fc2fcc77979898757fbbdb4807
   8005-agv-onboard-hmi @ 60f341878a1bce705598aa9043234c1ca2fb1d7a
   slots-simulator @ fb5f7c593742bf98bc3957b8729a38aad5321f28
   ```

   control-server 的产品代码与 4cd7525b 相同，5ee337fd 之后只加了证据。
3. 停止条件（`RIG_COMMIT_GUARD|RIG_DESKTOP_LOCK|RIG_DEADLINE|NOT_STARTED_|DESKTOP_LOCK_BUSY`）在作业日志里命中 9 行，全部带字面 `^[[36;1m`，是源码回显。不带回显标记的命中为 0。
4. 6 次全部 PASS，见 `SUMMARY.md` 与各运行目录。

## 留了什么

每次运行只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，外加 `commits.json`。日志、快照与内存采样没有入库，留在 artifact 里。
