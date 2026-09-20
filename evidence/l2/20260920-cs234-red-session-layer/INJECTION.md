# 这份证据不是干净提交跑出来的

`SUMMARY.md` 里的 `controlServerCommit` 与绿证据那份**一模一样**——L2 编排器记的是工作树所在的提交，
而注入是工作树上未提交的改动，它看不见。所以单看那一行会以为这是一次干净运行。这个文件就是为了这句话存在。

## 注入了什么

`src/ControlServer.Host/Transport/OnboardTcpServer.cs`，读取不再设窗口：

```diff
-                    idle.CancelAfter(liveness.Remaining);
+                    idle.CancelAfter(Timeout.InfiniteTimeSpan); // FAULT INJECTION cs#234: never time out
```

一行，只改会话层这一端。引擎那一端原样保留。

## 红在哪里，为什么是这一条

`L2-SL-02` 超时：假车载端的 readiness 一直停在 `READY`，服务端不关这条静默的连接。场景在这里中止，
后面的判据没跑。

**这正是要它证的：关连接这件事由会话层那一行负责，别的地方都不负责。**

## 还原

用文件备份还原（不是 `git checkout --`，那会把同文件里未提交的真实改动一起抹掉）。还原后核对过
`git hash-object <file>` 与 `git rev-parse HEAD:<file>` 逐字节一致——证明注入既不多也不少。
