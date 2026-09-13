# W1 现场窗口 2026-09-13：两次没有走完的尝试

结论：**两次都没有写任何核对记录，也没有写生产库。**完整的一轮在
[`../20260913-W1-three-vehicle-qualification/`](../20260913-W1-three-vehicle-qualification/)（W1 PASS）。
这里原样保留两次中途停下的记录，不删、不改。

## 一、14:34 交互式探针，没有开锁

目录：`interactive-agv01/`（原 `scripts/field/records/20260913-W1`）

产品负责人在控制端自己的终端里跑了当时的交互式 `Invoke-W1SlotIoProbe.ps1`。安全确认要求输入车号 `agv01`，
输入的是「确认」，探针在读模块之前停下，写 `raw/agv01/aborted.json`（`vehicle-safe not confirmed`）。**没有读模块、
没有开锁。**之后两次同命令重跑都因为 `raw/agv01` 已存在被拒（记录只增不改），同样什么都没做。

随后产品负责人决定由 agent 直接运行探针、现场不拍照，探针改为无键盘流程（`2f2f80d0`）。

## 二、14:50 agv01 第一次无键盘探针，1 号仓弹开后停下

目录：`run2-agv01-transaction-id/`（原 `scripts/field/records/20260913-W1-run2`）

14:50:54 开 1 号仓，14:51:04 停下，`raw/agv01/aborted.json`：

```
Invalid Modbus TCP response header (transaction 0, protocol 0, unit 255, length 5).
```

产品负责人报告：**1 号仓门弹开过，已手动关上。**停下后立刻只读读模块：输出全 0、八仓锁闭、八仓无物。

根因（只读压测复现）：在 agv01 上一条连接连续读模块，读取间隔 50 ms 与 200 ms 两轮都恰好在第 128 次读取出错。每次读取
两条请求，也就是第 256 条请求；应答事务号为 0。**这台康耐德 C2000 模块只回写 Modbus TCP 事务号的低 8 位**，16 位递增的
计数器过了 255 就与应答对不上。slots-simulator 完整回写 16 位，所以控制端彩排没发现。

修复 `aeadd667`：事务号在 1～255 循环。同一模块上连续读 300 次（600 条请求）0 错误，之后三台车完整的一轮都走完。

同一问题在车载端 `ModbusTcpIoModuleClient` 里同样存在（16 位递增、不匹配即断开重连、计数器不重置），另开车载端 issue 跟踪。

## 三、与本窗口相关的一件生产事实

15:07:41 产品负责人开启了线上 `JourneyRuntime`（配置备份 `appsettings.Production.json.bak-20260913150741`，服务随即重启），
15:07:46 引擎为 agv01 建了旅程 `f3399f62`、15:07:48 RIoT 移动订单 `CONFIRMED`。这发生在三台车探针全部结束（15:04:14）
之后、W1 窗口脚本写库（15:08 起）之前，与逐仓开锁核对没有时间重叠；W1-04 的现场观察是产品负责人在每台车做完时的报告。
