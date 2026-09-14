# L2 CI：`fp/b2-close@7327ef3a` 上批次 2 轨 B 三个出口场景与急停单发**各 3/3 `PASS`，逐场景证据齐全**

GitHub Actions run [`34815736635`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/34815736635) 第 2 次尝试（`l2.yml`，`workflow_dispatch`，runner `win11-01` headless），
job 与 run 结论均为 **`success`**，`l2-evidence` artifact `10337920571` 上传成功（2026-12-13 过期）。

**它取代同日 `../20260914-ci-34807641700-track-b-log/` 作为轨 B 在新代码上的现行证据**：那一次场景同样全过，但上传证据时 artifact 存储配额已满，只留下了运行日志。
本 run 第 1 次尝试（15:11）为同一原因失败；仓库随后改为公开、旧发布包清理后重跑，第 2 次上传成功。两次尝试的场景结果相同（26/26 `PASS`）。

## 身份

- `headSha` `7327ef3a`：服务端 `src/`、`tests/` 与 G3 共享绑定 `052759bc` 相同，其后只有 G3 绑定与证据提交；同一提交已快进进 `fp/v2-impl`。
- 12 份 `assertions.json` 的 `identity`：`controlServerCommit` 为 `7327ef3a`，`batchId` 为 `batch-2`，`rig` 为 `SyntheticOnboard`；
  `protocolReleaseIdentity` 为 `8005-agv-protocol` `protocol-v1.0.0@9f22db8`，`profileId` `AGV_FULL_PRODUCT`，manifest `a0e1deed…`，schema bundle `885191e7…`，vectors `51c5aaca…`，**`APPROVED_RELEASE`**。

## 入库的 12 份

从 artifact 原样拷入，只经过 `.gitattributes` 的换行规范化（CRLF 转 LF），内容未改。

| 场景 | 目录 | 判据 | 结果 |
| --- | --- | --- | --- |
| `three-vehicle-exit`（轨 B 出口：合成 3 车） | `../20260914-ci-34815736635-three-vehicle-exit-01`～`03` | 每份 22 条 | **3/3 `PASS`** |
| `command-surface-order-hold`（轨 B 出口：命令面该调才调、参数正确、只调一次） | `../20260914-ci-34815736635-command-surface-order-hold-01`～`03` | 每份 20 条 | **3/3 `PASS`** |
| `route-graph-staleness`（轨 B 出口：引擎快照陈旧态 fail-closed） | `../20260914-ci-34815736635-route-graph-staleness-01`～`03` | 每份 13 条 | **3/3 `PASS`** |
| `emergency-stop-single-trigger`（票 19 的 L2 前置：急停只发一次） | `../20260914-ci-34815736635-emergency-stop-single-trigger-01`～`03` | 每份 14 条 | **3/3 `PASS`** |

三个出口场景的判据条数与 9 月 12 日 `../20260912-ci-34701119449-*` 逐场景相同。

同一 artifact 里另有 14 份没有入库：批次 3 的 `slot-configuration-activation-replay`、`onboard-alarm-snapshot-dashboard` 各 3 份，以及 8 个单次场景，均为 `PASS`，逐行见 `run.log`。

## 本目录

- `run.json`：第 2 次尝试的 run 元数据（`headSha`、`attempt`、各步骤结论）。
- `run.log`：第 2 次尝试的完整运行日志；checkout 写入的凭据头已被 GitHub 屏蔽为 `***`。

## 未在本轮证明的

- 合成对端，不是真车载端与真 RIoT；急停在真车上的停车事实属于票 19 的现场演练，仍挂在生产切 v2 的现场窗口。
