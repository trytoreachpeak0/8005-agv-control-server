# 这份自检自己有没有判别力（control-server#204）

调度审查问的：**把被测模块改坏，这些自检有几条会红？如果答案是「都不会红」，它就是摆设**——
而我们刚为把它挂进 CI 批了一次越界。

做法是轻量变异测试：对 `L2HmiPhraseWatch.psm1` 逐个注入一处改动，每次只改一处，跑一遍自检，数有几条红。

| 把模块改成 | 红 / 10 | 红的是哪几条 |
| --- | --- | --- |
| 把读失败的轮算回干净轮 | **2** | a round in which one element could not be read is not a look；a sighting in a failed round is still a sighting |
| 整轮失败也算干净轮 | **6** | a round in which one element could not be read is not a look；a tree that will not enumerate is a failed round, not an exception；a window that is gone is a failed round, not a silent nothing；a tree that enumerates to nothing is not a look either；a sighting in a failed round is still a sighting；a business probe carrying a failing scan still returns its business value |
| 干净轮计数永远是个大数 | **7** | a round that read every element is a clean look；a round in which one element could not be read is not a look；a tree that will not enumerate is a failed round, not an exception；a window that is gone is a failed round, not a silent nothing；a tree that enumerates to nothing is not a look either；a sighting in a failed round is still a sighting；a sampling window stops at the first sighting |
| 采样走完窗口又写 Not reached | **1** | a sampling window that ends without a sighting reports a reading, not "Not reached" |
| 检出不记进 Seen | **3** | a sighting in a failed round is still a sighting；a sampling window stops at the first sighting；a sampling window on a watch that already has a sighting still runs its full length |
| 扫描重新往外抛异常 | **2** | a tree that will not enumerate is a failed round, not an exception；a business probe carrying a failing scan still returns its business value |
| 空树又算干净轮 | **1** | a tree that enumerates to nothing is not a look either |
| deadline 误算成毫秒 | **2** | a sampling window that ends without a sighting reports a reading, not "Not reached"；a sampling window on a watch that already has a sighting still runs its full length |
| 提前退出看累计检出（轻微 7） | **1** | a sampling window on a watch that already has a sighting still runs its full length |

## 这张表证明的是什么，不证明什么

**它证明的是「这九个方向有护栏」，不是「这个自检是完备的」。**

第一版这张表只有七行，我当时写的结论是「七种全中、零漏，所以自检有判别力」——**那个说法过头了**。
独立审查想到了第八个方向（把采样循环改成扫两轮就退出、或把 `deadline` 算错成毫秒），而当时那个方向
是空的：8 条自检全绿，**而真装置那边 `L2-DA-09` 的 ≥10 轮靠后面三处业务探针也能凑够，所以真装置也
照样绿**——正好落在 `test.yml` 那条注释声称的保护范围里。补下界并加断挂钟之后，那两种改法各红 1 条。

**变异测试只覆盖你想得到的变异。**方法仍然值得做成固定动作（它比「我觉得这些用例挺全的」硬一个量级，
成本也低），但结论要写成「以下这些方向有护栏」，不能写成「自检有判别力」。

最后两行是本票新立的两条红线自己的护栏（[[new-limit-needs-its-own-guard]] 的要求）：空树那条、以及
采样窗提前退出看增量而非累计那条。**新立的限制必须有东西在它失效时报警**，否则它只是一句注释。

## 附：空树那条红线是怎么发现的

审查点名问「`L2HmiPhraseWatch.psm1` 在什么都没扫到时报什么」。实测三种空输入：

| `$ElementSource` 返回 | 补之前 | 补之后 |
| --- | --- | --- |
| `@()`（空数组，被管道展开成 `$null`） | 失败轮 ✓ | 失败轮 |
| **`, @()`（不展开，是个真正的空集合对象）** | **干净轮 31 次，`L2-DA-09` 判据 PASS** ✗ | 失败轮 |
| 空 `ArrayList`（像 UIA 的空 collection） | 失败轮 ✓ | 失败轮 |

中间那一格就是这条判据要防的那种假绿：**「扫了 31 轮、零检出」可以是「树空了 31 轮」**。

补之前它在前后两格里是对的，**但对得偶然**——靠的是 PowerShell 把空集合展开成 `$null`，而展开与否
取决于调用点怎么写（`, @()` 就活下来了），调用点改个写法就会翻面，而且翻面时不会有任何东西变红。
补上那一行之后，保证从「PowerShell 恰好会展开」搬到了「代码明确要求这一轮读到过东西」。

**真装置实测的元素数坐实了这条红线的前提**：CI run `35516023239` 三次运行里，干净轮最少的一轮分别
读到 114、114、113 个元素。真实窗口树有上百个元素，所以「读到 0 个」确实等于「没读到」，而不是
「树本来就这么小」。
