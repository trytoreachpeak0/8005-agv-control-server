# AbsentAtObservation 单次真实建单人工 Runbook

本 runbook 只推进一次真实网络事务：对现有 permit 绑定的取货订单最多执行一次
`POST /api/order/v1/add/byDefaultMissions`，随后只读对账。它不判断行人、避障、急停或现场物理安全；这些由现场人员和车辆安全系统负责。

## 冻结输入

- 产品：`8577b413697996a6dad4e57953eefa77f86100cd`
- package manifest：`30b33a60ac0f7fcda88353d2179a68f74e8d7ad5f1bdc6562c28e695b92a52e9`
- Onboard：`84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`
- simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- private permit SHA-256：`0ab511f46c32e3aaa3b9c30c95e7b8c6ee92ebdffafa97743908aa143c7d3be0`
- selection SHA-256：`34603116f9b93454c6263704124fa0861a017b94bdef3ce43090e704755452ea`
- stopped shadow DB bundle SHA-256：`9c12e959dd549757fce3921f326de679e4f4d5ff83dc9e6ab64c31f859701b5d`
- RIoT upstream：`http://172.19.206.222:8888`

任一值不符即停止。permit、数据库和凭据只留在本机受限位置，不提交到 Git。

## 执行

1. 现场负责人先给出物理安全 GO；没有 GO 就不启动任何进程。
2. 在新的临时目录复制已停止的完整 shadow DB bundle，并复用
   `scripts/Invoke-AuthorizedAbsentObservationShadow.ps1` 已验证的隔离 Host、Onboard、simulator
   启动参数与 `finally` 清理顺序。不得使用 production DB。
3. 从 private permit 原样读取 demand、upper-id、movement-leg、两代 generation；从 shadow DB
   原样读取车辆、地图和目的站。禁止人工改写任何身份。
4. 为本次执行生成一个新 UUID 和不超过 15 分钟的 expiry，通过
   `RiotAbsentAtObservationCreateExperiment__*` 环境变量启用产品内置的一次性门；真实 RIoT
   凭据只放进本次隔离 Host 的进程环境。
5. 启动 Onboard、simulator，最后只启动一次 Host。观察 SQLite 审计：只有精确
   `PRE -> ARM -> START` 链出现后才可能发生 POST；`CREATE_REQUEST` 一旦出现，本次授权即已消耗，
   不得重启 Host 或再次尝试。
6. 得到创建响应后，只用同一 upper-id 做只读查询。精确订单身份存在即记
   `orderConfirmed=true`；超时、断连或结果不明确时先停止 Host，之后只允许继续只读对账，
   禁止第二次 POST、cancel 或其它写请求。
7. `finally` 按 Host、Onboard、simulator 的顺序停止本次进程，确认临时端口全部释放；保留
   shadow DB 和日志供人工复核。

结束时只记录：

```json
{"sent":null,"orderConfirmed":null,"writeCount":null,"cleanupPassed":false}
```

`null` 表示无法证明，不得改写为 `false`。`writeCount` 只能是 `0`、`1` 或 `null`。

## 立即停止条件

- 冻结输入、订单对象或 route 任一不一致；
- 已存在同 upper-id 订单；
- 出现第二个写请求、Host 重启请求或任何 cancel 请求；
- POST 超时、连接中断或响应无法确认；
- cleanup 未能停止精确子进程或释放临时端口。

## 已知剩余风险

- upstream 是明文 HTTP，凭据和响应没有传输层机密性或端点身份保证；
- at-most-once 依赖产品持久 permit、审计链和操作者不重启，缺少独立网络 egress interlock；
- 本 runbook 只证明网络事务与订单身份，不证明车辆到站或现场安全。

上述风险必须在新的逐次授权中被明确接受；旧授权不得复用。
