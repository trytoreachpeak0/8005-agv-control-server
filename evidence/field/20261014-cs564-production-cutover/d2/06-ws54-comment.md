## 第 1 步：agv02 上两个注册表值的类型（只读，2026-10-15 10:0x，C11 安排在生产切换现场会话里顺带查）

只读查询，没有运行 `07-fix-clock.ps1`，也没有写入。

```
MaxPosPhaseCorrection kind=DWord clrType=System.Int32 raw=-1 hex=0xFFFFFFFF
MaxNegPhaseCorrection kind=DWord clrType=System.Int32 raw=-1 hex=0xFFFFFFFF

HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\Config
    MaxPosPhaseCorrection    REG_DWORD    0xffffffff
    MaxNegPhaseCorrection    REG_DWORD    0xffffffff
```

两个值都是 `REG_DWORD 0xFFFFFFFF`，.NET 读出来是 Int32 的 -1，正是票里说的「正常情况」。所以 `:129` 的 `[int] $item.$Name` 在今天的 agv02 上不会抛错。

10-13 那次报错 `Cannot convert value "4294967295" to type "System.Int32"` 不是这两个值的类型引起的。有两种可能：报错发生在这两个值还没被写成 DWORD 之前（例如当时是另一种类型，后来某次运行把它改写了），或者是票里列的另一条路径（退出码，或别处的 `[int]` 转换）。完整报错原文和行号仍然没有，这一项要在下次复现时取得。

证据：控制端 `F:/8005-scratch/cutover-ev/production-20261014/d2/03-ws54-agv02-w32time-kinds.txt`。
