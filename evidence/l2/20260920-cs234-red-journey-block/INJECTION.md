# 这份证据不是干净提交跑出来的

`SUMMARY.md` 里的 `controlServerCommit` 与绿证据那份**一模一样**——L2 编排器记的是工作树所在的提交，
而注入是工作树上未提交的改动，它看不见。所以单看那一行会以为这是一次干净运行。这个文件就是为了这句话存在。

## 注入了什么

`src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs`，引擎不再判失联：

```diff
-        if (await NameSilentOnboardSessionAsync(runtime, session, now, cancellationToken).ConfigureAwait(false))
+        // FAULT INJECTION cs#234: the engine never names a silent session.
+        if (false && await NameSilentOnboardSessionAsync(runtime, session, now, cancellationToken).ConfigureAwait(false))
         {
             return;
         }
```

只短路引擎这一端。会话层那一端原样保留。

## 红在哪里，为什么是这一条

- `L2-SL-02` **绿**：服务端照常关掉了静默的连接。
- `L2-SL-04` 超时：看板阻断端点始终不列这条旅程。场景在这里中止。

**两端分两次注入，就是为了让红点指得到具体一端。** 一次全拿掉的话，两端的缺失会混在同一个超时里，
看不出是谁没做。

## 顺带记下的一件事，它改变了本票的实现位置

`logs/control-server.out.log` 里有 **60 条**
`System.IO.IOException: No recovered Onboard peer is connected for 'AGV-L2-001'`，每秒一条，堆栈是
`JourneyRuntimeEngine.AdvanceAsync` → `OnboardJourneyPublisher.ReplayPendingForSessionAsync` →
`OnboardPeer.SendAsync`。

连接一没，重放就抛，整轮 fail-closed——票面要求把阻断码写在「到站不可信」之后的分支里，而那些分支
在这种情况下根本走不到。判定因此挪到了推进之前。**这个位置不是随手选的，是这份日志选的。**

## 还原

用文件备份还原（不是 `git checkout --`，那会把同文件里未提交的真实改动一起抹掉）。还原后核对过
`git hash-object <file>` 与 `git rev-parse HEAD:<file>` 逐字节一致——证明注入既不多也不少。
