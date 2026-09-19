# 一次性场景副本

`real-onboard-inflight-load-reconnect` 不是 `scripts/l2/scenarios/` 里的正式场景。本目录的两个场景文件逐字取自车载端仓
`8005-agv-onboard-hmi@44b3aa6e` 的 `evidence/hmi-127/green/rig-inflight-load-reconnect-d21b3e8-001/scenario/`（副本来源 onboard-hmi#131），
`.ps1` 的 SHA-256 以 `e3bdf006a309b949` 开头，与实际运行的副本一致。

跑法：把服务端 `76c2ca21` 的 `scripts/l2/` 与 `scripts/DesktopLock.psm1` 复制到临时目录，这两个文件放进副本的 `scenarios/`，
运行副本的 `Invoke-L2Scenario.ps1`，显式传 `-Repository`（服务端 detached worktree，`76c2ca21`）、`-OnboardRepository`（`44b3aa6e`）、
`-SimulatorRepository`（`fb5f7c59`）。转为正式场景见 control-server#205。
