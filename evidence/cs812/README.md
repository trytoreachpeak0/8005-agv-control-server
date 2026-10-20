# control-server#812 证据

本机：7945HX 笔记本，系统区域 en-US（ACP 1252、OEMCP 437），2026-10-20。

## unit/

`ServerConsoleEncodingTests`：起真 Host（隐藏控制台，与 L2 下一样），读重定向输出的原始字节。

- `red-34065c1a.txt`：测试提交上（修复前）红。等锁日志里的库路径 `…\关卡\controlserver.db` 写成 `5C 3F 3F 5C`（`\??\`）。
- `green-62e8cda3.txt`：修复后，连同 `ControlServerDatabaseLockTests`、`ArchitectureTests` 共 92/92。
- `green-three-tests.txt`：三条（UTF-8 无 BOM；未处理异常退出前最后一行 `Startup refused …关卡…` 已落盘；退出前 `Console.WriteLine` 的最后一行已落盘）。
- `mutation-autoflush-off.txt`：把两处 `AutoFlush = true` 改成 `false`，第三条红（`Package capacity import complete` 整行丢失），另两条仍绿：Serilog 的 Console sink 每条事件后自己 flush，`AutoFlush` 保住的是 Serilog 以外的直接写。
- `green-four-tests.txt`、`mutation-no-seterror.txt`（审查 S2）：第四条 `RedirectedStandardErrorIsUtf8` 经环境变量再挂一个全部写 stderr 的 Serilog Console sink，等锁行经托管 `Console.Error` 写出，「关卡」是 UTF-8。删掉 `Console.SetError(...)` 那一行，这条红在 `5C3F3F5C`，另三条仍绿。
- 不在本票范围（审查 S1）：运行时自己打印的未处理异常文本（`Unhandled exception. ...`）不经 `Console.Error`，按控制台代码页写，en-US 上 stderr 里的中文仍是 `?`。

## selfcheck/

- `red-4e77a360.txt`／`green-bd6fa548.txt`：`scripts/l2/Test-L2TaskTypeStations.ps1` 新加两例，同一个 `Read-L2ProcessLog` 喂 UTF-8 与 GBK 样本都读出「关卡」。修复前 GBK 那例按本机 OEM 代码页 437 读成乱码。
- `script-static-checks.txt`（32/32）、`scenario-sql-explain.txt`（22/22，走改过的 `--migrate-only` 启动）。

## l2-scenario/

`task-type-binding-station-reused-refuses-start`，本机单跑。`red/` 在 `34065c1a`（修复前）FAIL，`green/` 在 `62e8cda3` PASS。`control-server.out.log` 里 `Station <站名>/210`：

```
red    53 74 61 74 69 6f 6e 20 3f 3f 2f 32 31 30               Station ??/210
green  53 74 61 74 69 6f 6e 20 e5 85 b3 e5 8d a1 2f 32 31 30   Station 关卡/210
```

## l2-full/

`RESULT.txt`：l2.yml 默认模式全量，本机 4 路，`bd6fa548`，107 次 107 PASS。同一台机器上 cs#804 的改前基线（`1f31a4a4`）是 106 PASS、1 FAIL，FAIL 的就是上面这个场景；vm01 基线（run 39264072063）全 PASS。

## codepage/

#805 审查的问题：FieldOps 设 `Console.OutputEncoding = UTF-8`（`SetConsoleOutputCP(65001)`），服务端输出会不会取决于之前有没有跑过 FieldOps。`Test-ConsoleCodePageIndependence.ps1` 在一个有控制台的 pwsh 里，各起四次 Host（占着锁，让它写出带「关卡」的库路径）：`Start-L2Process` 起（与 L2 一样）、`Start-Process -NoNewWindow` 起（与本控制台共用，最坏情况），各在 FieldOps 跑之前、之后一次。本控制台代码页 FieldOps 前 437、后 65001。`bytesBeforeFileName` 是 `controlserver.db` 前 8 字节。

| | L2 前 | 共用前 | L2 后 | 共用后 |
| --- | --- | --- | --- | --- |
| `old.json`（`34065c1a`） | `\??\` | `\??\` | `\??\` | `\关卡\` UTF-8 |
| `new.json`（`62e8cda3` 起） | `\关卡\` UTF-8 | 同左 | 同左 | 同左 |

旧代码下，L2 的起法给服务端一个新控制台，FieldOps 改不到它，前后都是 `??`；只有共用控制台、且先跑过 FieldOps 时才碰巧是 UTF-8。修复后四种都是 UTF-8，与控制台代码页无关。
