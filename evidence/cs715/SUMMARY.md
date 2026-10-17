# cs#715 证据：安装器删目录前等进程退出、占用类错误有界重试

全部在本机临时目录验证，不连车、不碰 factory01、没有装任何服务。

## field/ —— 现场原始输出（从 `F:/8005-scratch/cutover-ev/` 复制）

- `step8-deploy.txt`：切换试跑第 8 步首装（2026-10-13 17:44），安装器在「退役旧假 MES」时失败，原文
  `Remove-Item: Access to the path 'C:\Program Files\8005 AGV\ControlServer.V2.FakeMesIngest\clrjit.dll' is denied.`
- `step8-after-fail.txt`：失败后的现场状态，假 MES 进程与计划任务已没，目录剩 371 个文件，V2 服务与安装目录未变。

## red/ —— 测试提交 `a8e2d91b` 上的失败原文

`test-commit-directory-removal.txt`：20 条里 19 条失败。关键两条是缺陷本身：子进程占住文件或映射 DLL 时，
现状一次就失败、零次重试（`waits=0`），其中映射 DLL 的报错与现场同形：
`Access to the path '...\HeldDll.V2\mapped.dll' is denied.`

## green/ —— 实现提交上的结果

- `directory-removal.txt`：`Test-ParallelInstanceDirectoryRemoval.ps1` 20/20。其中用尽重试的报错原文：
  `Could not delete '...\NeverReleased.V2' after 10 attempts over 9 s: '...\NeverReleased.V2\held.bin' is still in use (The process cannot access the file ... because it is being used by another process.). Held by: pid 31288 (pwsh).`
- `test-parallelinstance-summary.txt`：主自测 `Test-ParallelInstance.ps1` 1201/1201（基线 1199，多出的两条是新脚本的解析检查与调用）。

## 审查（#762）之后

按审查改了 S1（拒绝访问且查不到占用进程时报「可能是权限或属性问题」）、S2（出错条目不为空就写进主句，目录非空时列出剩余条目）、
L1（停进程前先读 `.Handle` 钉住进程）、L4（README）。`green/directory-removal-after-review.txt` 新自测 24/24，
`green/test-parallelinstance-summary-after-review.txt` 主自测 1201/1201。

## mutation/ —— 变异结果，每个文件含变异 diff 与完整输出

| 变异 | 被杀的用例 |
| --- | --- |
| m1 去掉重试（占用类错误也直接抛） | 子进程占文件、占 DLL、用尽重试、每次复核，共 4 条红 |
| m2 只在第一次尝试做路径复核 | 「每次重试前都复核路径」红 |
| m3 所有错误都重试 | 5 条「不应重试」的分类用例红 |
| m4 等进程超时只告警不抛 | 「超时报进程号」红 |
| m5 用尽后报错不写文件名 | 「用尽重试」红 |
| m6 部署新假 MES 处去掉等待 | 该调用点源码顺序用例红 |
| m7 退役处换回吞超时的 `Wait-Process` | 该调用点源码顺序用例红 |
| m8（审查后）拒绝访问且无人占用仍报「仍被占用」 | 「权限或属性问题」措辞用例红 |
| m9（审查后）出错条目不是现存文件时丢掉名字 | 删除挂起文件、目录非空两条红 |
| m10（审查后）部署处去掉 `.Handle` 钉住 | 该调用点源码顺序用例红 |

m1 下「安装器顺序（停进程→等退出→删）」那条仍绿：本机上等到进程退出后映像已解除映射，不需要重试。
现场那次正是等完仍未解除，所以两层都要；这条用例证明的是等待这一层。
