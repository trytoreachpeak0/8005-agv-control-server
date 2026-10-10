# in-transit-door-not-locked 的红证据（本机，合成车载端）

- 代码：提交 `6e10044e`，加上两处未提交的改动：
  1. 注入：`JourneyRuntimeEngine.ObserveDoorsInTransitAsync` 第一行 `return false`，即把门锁从故障模型的输入里拿掉；
  2. 场景文件里原因码数组显式成 `string[]` 的修正（随后作为 `887aff17` 提交，与注入无关）。
- 预期（跑之前写下）：L2-DL-01 通过；场景在「等服务端发出急停」那一步超时失败。
- 实际：`FAIL Timed out after 90s waiting for: the server issued the emergency stop. Last observed: 0`；L2-DL-01 PASS。
- 注入用备份还原，`git status` 里没有 `src` 改动；之后 `dotnet build ControlServer.sln -c Release --no-incremental`。
- 同一段代码去掉注入后的绿：旁边的 `...-green-887aff17`（11/11 PASS）。
