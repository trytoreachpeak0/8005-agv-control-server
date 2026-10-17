## 生产切换（2026-10-14 晚）第 0～4 步、第 17～18 步

现场会话执行，证据在控制端 `F:/8005-scratch/cutover-ev/production-20261014/`。

- **第 0 步只读摸底（17:47）**：V2 Running/Manual，闸门 `RiotCreateDispatch.enabled=False`，没有非终态旅程，没有 Blocked 车；区表是试跑末尾导入的空表（v10，0 行），最后一次受理在 11:04；MVP Stopped/Disabled。RIoT 上三车 IDLE，电量 agv01 89、agv02 79、agv03 50。三车车载栈都没在跑。三车与 factory01 时差 +121 ms、+116 ms、-39 ms。
- **第 16b、17 步**：受理在试跑末尾已经停了（空表）；20 号 `-State Closed` 回读 PASS（本来就是关的），MVP 没被动过。
- **第 17a 步**：`19-archive-onboard-journal.ps1` 三车都是 `pending=0 loadsOnBoard=0 activeUnlockSlots=[]`，`8005AGV` 已改名为 `8005AGV.pre-cutover-production-20261014`。agv01 这一步执行前用户已授权。
- **第 18 步（17:59）**：V2 停服，`C:\ProgramData\8005\ControlServer.V2` 改名为 `ControlServer.V2.pre-cutover-production-20261014`（库 A `controlserver.db` SHA-256 `C6F69D939A1B32B359639B4D7019400B00B48A6EB9139BB38CC58646C78E0CC2`），新建空目录、ACL 与原目录相同，没有起服。
- **第 4 步凭据**：`Set-ControlServerSecret.ps1 -NewEmergencyReleaseCredential Empty, Loaded` 已生成，`-Show` 两把都是 present。现场人员角色名单只有一条 `S00*****`（R-11、R-13），用户确认是本人，名单保持不动，持 R-13 的只有一人。
- **第 19 步预演**：19 号 `-ProductionCutover -WhatIf`，S1 运行时关、run 38542183893：包哈希 `f17f3331…` 已核，`PACKAGE_COMMIT_MATCHES_CLONE=f12bc809`，两把急停解除凭据都 present。正式安装等生产包 G3 结论。
