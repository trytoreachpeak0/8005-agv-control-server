# REQ-0178 真车补测：已暂停（2026-10-12 14:2x），等 cs#649

**结论：没测成。**部署 V2 时连续撞上两个部署脚本缺陷，按调度 C10 的决定暂停。等 cs#649（两个缺陷都并到了这张票里）合入后再做。车一直没动，没有开过闸，没有起过任何车载端栈，没有改过任何车载端配置。

## 卡在哪里

| 步骤 | 结果 | 证据 |
| --- | --- | --- |
| 出包 | release run `38210567927` 成功；manifest 中 controlServer 为 `4771f70e`，等于部署 worktree 的 HEAD；onboard 为 `b2f602ec`；zip sha256 `489d26d8…` | `06` |
| 19 号升级（off 版定义，覆盖为 5） | **失败**：`Update-ControlServerLocal.ps1:232` 报 "Safety projection returned HTTP 401."。读到的：升级自检用机器级共享的 `CONTROL_SERVER_ONBOARD_CREDENTIAL` 调 `/api/onboard/v1/vehicle-safety`。推测：两车实例从 cs#600 起改成按车凭据，共享凭据被拒。升级脚本自动回滚到旧二进制、运行时关闭；`installed-instance.json` 为 RESTORED。已开 **cs#649** | `07` |
| 19 号卸载（保留数据） | 成功；`C:\ProgramData\8005\ControlServer.V2` 与库保留 | `08` |
| 19 号全新安装（off 版定义） | **失败**：`Install-ControlServerLocal.ps1:516` 报 "The property 'reason' cannot be found on this object."。读到的：`:541` 在严格模式下读取 `$firstReadiness.reason`，而首次 `/health/ready` 回 `ready` 时响应里没有 `reason` 字段。推测：昨天 prep6 用的是全新空库，第一次一定是 not-ready 并带 reason，所以没撞上；今天沿用 newdb5、运行时开着，服务一起来就 ready。安装回滚，服务被移除。这个缺陷也已并入 cs#649 | `09` |

## 为什么停下而不绕过

调度 C10 在三条可选路线里选了「先修脚本」：
- 部署后手改 appsettings 打开开关：这会绕过安装器的生效配置回读，而本次补测要验证的正是这道回读。
- 再加一份 runtime=false 的引导定义，先装上，再用 off/on 版重装：重装走的是升级路径，仍会撞上 401。
- V2 保持未安装，不用旧包恢复：上午收工时它本来就停在 Manual 状态，没有在为任何东西服务，数据目录也还在。

## 当前状态（14:2x 只读回读，`10`）

- V2 服务不存在（服务注册表键也不存在）；安装目录不存在。
- `installed-instance.json` 为 RESTORED，是上一次的记录：runtime=false，VFR=false，没有 clearance，`riotCreateDispatch.enabled=false`。
- 数据目录与库都在，库最后写入时间 12:10:41（上午收工时）。独占 2 行（等待点占用，与收工时相同）；未恢复暂停 0；未结束周期 0。
- agv02、agv03 的车载端与模拟器进程数都是 0。agv02 的车载端**没有**部署 `b2f602ec`。
- MVP 为 Running，pid 10912，没有碰过。
- 13:42 只读 RIoT 的 agv03 读数为 pos 0、`sys=ERROR`，按调度要求只做记录，没有碰。

## 恢复时要做的

cs#649 合入后，用包含修复的提交重新出包，按 `PLAN.md` 从 A 段开始。两份定义（`instance-factory01-v2.req0178-field-on.json`、`-off.json`）在本分支上，也需要随修复一起合入，或者在那时重新核对。
