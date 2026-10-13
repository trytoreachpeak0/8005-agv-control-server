# cs#588 证据索引

票：control-server#588。建单闸门冲突日志 2112 的模板写「站 {TargetStationId}、车 {AgvId}」，调用处按「车、站」传参，结构化属性互相装错；顺带扫其余 `LoggerMessage.Define` 的参数顺序。只影响日志，不影响判定。

## 提交顺序

| 提交 | 内容 | 本机结果 |
| --- | --- | --- |
| `5bef52228` | L1：`CreateGateTests.TheConflictLogNamesTheVehicleAndTheStationUnderTheirOwnProperties`，用捕获结构化 state 的日志器断言 2112 的四个属性 | 红，`l1-red-5bef52228.txt` |
| `2a0865216` | 修 2112：模板措辞不动，泛型参数与调用顺序改成 DemandId、TargetStationId、AgvId | `CreateGateTests` 49/49 |
| `a2b3111a6` | 护栏 `LoggerMessageArgumentOrderArchitectureTests`（两轮扫描、下限断言、合成样例自检），登记切片账本 | 红，报三处既有错位，`guard-red-a2b3111a6.txt` |
| `538987f08` | 修 2256、2108、2201 三处 | ArchitectureTests + CreateGateTests 130/130 |
| `1295b8c07` | 护栏另打印「名字与任何占位符都不同」的实参数 | 130/130，`green-1295b8c07.txt` |

## 文件

- `l1-red-5bef52228.txt`：修复前 L1 的失败行。`AgvId` 期望 `AGV-01`，实际是站号 `12`。
- `guard-red-a2b3111a6.txt`：护栏在三处既有错位未修时的失败行与两轮计数。
- `green-1295b8c07.txt`：最终提交上护栏的完整输出，含两轮计数和第二轮后仍认不出的 49 个实参原文。
- `mutations.txt`：两个变异。M1 把 `PreCreateGate.cs` 换回修复前版本，护栏与 L1 都红；M2 对调 2180 调用处同为 `string` 的 `row.AgvId` 与 `row.DeviceKey`（编译器不会提示），护栏红。两次都在提交后做，只改并只还原那一个文件，还原后 `git status` 干净。

## 两轮扫描的计数

| | 定义 | 调用处 | 实参 | 认出名字 | 认不出 | 认出但与任何占位符都不同名 | 错位 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 第一轮 | 206 | 225 | 748 | 636 | 112 | 104 | 修前 3 处（2112 已先修） |
| 第二轮 | 206 | 228 | 761 | 712 | 49 | 129 | 修前 3 处，修后 0 |

第一轮只认标识符与成员链，只在定义所在文件找调用。第二轮再认去掉链中 `!`、开头强转、`?? 兜底`、尾部 `.ToString(...)`／`.Value` 一类的写法，并在同一分部类的其他文件里找调用（多出 3 个调用处）。两轮后停，没有再追。

## 扫出的错位与处理

全部在本票改，模板措辞与事件号都没动，改的是泛型参数与调用顺序：

| 事件 | 位置 | 修前装错的属性 |
| --- | --- | --- |
| 2112 `LogConflict` | `src/ControlServer.Host/Runtime/CreateGate/PreCreateGate.cs:156` | `{TargetStationId}` 存车号，`{AgvId}` 存站号 |
| 2256 `LogChargingEndNotProven` | `src/ControlServer.Host/Runtime/JourneyRuntimeEngine.Charging.cs:372` | `{Since}` 存桩号，`{StationId}` 存时刻 |
| 2108 `LogStationDeadlineEndedStop` | `src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs:5829` | `{Deadline}` 存需求号，`{DemandId}` 存时刻 |
| 2201 `LogOwnAbandonedOrderHeld` | `src/ControlServer.Host/Runtime/ForeignOrders/ForeignRunningOrderSupervisor.cs:560` | `{Basis}` 存单状态，`{OrderState}` 存归属依据 |

开工时查过：这几个文件没有开着的 PR，远端最近 60 个分支也没有改它们的；都不在 `Runtime/Dispatch/Criteria` 下。

## 护栏看不到什么

- 两个实参名字都与占位符对不上时对调了：第二轮有 129 个实参属于「认出了名字、但与任何占位符都不同名」，护栏对它们不下判断。
- 认不出名字的实参表达式：第二轮后 49 个，主要是方法调用（`error.GetType().Name`、`string.Join(...)`、`RiotFailureDetail.Of(...)`）、字符串字面量、条件表达式、插值字符串。原文在 `green-1295b8c07.txt`。
- 委托在分部类文件组之外被调用，或被传走后换名调用。
- 模板不是普通字符串字面量或其 `+` 拼接。
- `[LoggerMessage]` 源生成写法的 12 处不扫：源生成器按名字匹配，对不上即报错（本仓 `TreatWarningsAsErrors`）。
