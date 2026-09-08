# 票 08：RIoT 白名单架构测试的验收证据

## 运行类型

纯本机 tier 1。**不动车、不建单、不使用任何现场凭据、不碰 RIoT、不需要桌面**。
全部是 `dotnet build` 与 `dotnet test`，跑在控制端笔记本上。

## 结论

| 项 | 结果 |
| --- | --- |
| 新增测试 | 7 条，全绿，第一次写完即绿 |
| 全量套件 | **395 passed / 0 failed / 0 skipped**（原基准 388 ＋ 新增 7） |
| 人为 `.Raw` | 变红（实验 A） |
| 人为清单外端点 | 变红（实验 B） |
| 人为触碰生成客户端 | 变红（实验 C） |
| 人为新增 `src/` 项目 | 变红（实验 D） |

守卫的当前基准与 `docs/riot-call-allowlist.md` 第四节逐条吻合：**产品代码调用 13 个 Facade
方法、12 个端点，全部 ⊆ 清单第一节的 26 条；`.Raw` 零命中。**

## 四个「变红」实验

判定做在编译产物的 IL 上，所以"人为加一处违规"是真的改产品代码、真的重新构建。四次实验
各自跑完即用 `git checkout --` 还原，工作树无残留。

### 实验 A — `.Raw` 逃逸

在 `HttpRiotRouteCostProbe` 里加 `public object RawEscapeExperiment() => riotSession.Maps.Raw;`。

```
ProductCodeReachesRiotOnlyThroughNamedFacades [FAIL]
  Product code reaches RIoT outside the named Facade surface, which REQ-0309 and section 2 of
  the allowlist forbid: ControlServer.Infrastructure reads MapClient.Raw;
  ControlServer.Infrastructure references RIoT.Sdk.Generated, the generated client, which
  section 2 of the allowlist denies product code

EveryRiotCallProductCodeMakesIsOnTheAllowlist [FAIL]
  Product code calls RIoT Facade methods that section 1 of the allowlist does not approve: get_Raw

Failed: 2, Passed: 5
```

两条同时变红是设计如此：`get_Raw` 既是 `.Raw` 检查的命中，也不在子集检查的放行清单里
——「忽略属性访问器」那条捷径正是本可以放跑它的写法。原文见
[`experiment-a-raw-escape.txt`](experiment-a-raw-escape.txt)。

### 实验 B — 清单外的具名端点

加 `riotSession.Order.PriorityExecAsync(...)`。`PriorityExec` 在 Facade 里，白名单第二节
明写「只保留为未来候选」。

```
EveryRiotCallProductCodeMakesIsOnTheAllowlist [FAIL]
  Product code calls RIoT Facade methods that section 1 of the allowlist does not approve:
  PriorityExecAsync

Failed: 1, Passed: 6
```

**只有一条红。**`.Raw` 检查保持绿——两条断言互不牵连，这正是要证的。原文见
[`experiment-b-unapproved-endpoint.txt`](experiment-b-unapproved-endpoint.txt)。

### 实验 C — 被拒方法泄漏生成类型

加 `riotSession.Device.ListDevicesAsync(...)`。它返回 `RIoT.Sdk.Generated.Device.Models` 里的
`Page_Of_DeviceObject`。

```
EveryRiotCallProductCodeMakesIsOnTheAllowlist [FAIL]
  ... does not approve: ListDevicesAsync
ProductCodeReachesRiotOnlyThroughNamedFacades [FAIL]
  ... ControlServer.Infrastructure references RIoT.Sdk.Generated, the generated client, ...

Failed: 2, Passed: 5
```

两条都红且**都说对了**：那个调用既不在清单里，又把生成客户端的类型拖进了产品代码，
而白名单第二节把「生成客户端直接调用」列为不批。原文见
[`experiment-c-generated-client-leak.txt`](experiment-c-generated-client-leak.txt)。

**顺带证实了一件事**：Facade 里唯一会把生成类型泄进调用方签名的公共成员，就是三个 `Raw`
属性和 `DeviceClient` 那两个被明确拒批的方法——**获批清单里的每一个方法都只返回
`RIoT.Sdk.Core` 类型**。所以这条腿在当前 SDK 上没有误报面。

### 实验 D — `src/` 下新增未被扫描的项目

`mkdir src/ControlServer.Experiment`（空目录即可，判定读的是目录列表）。

```
EverySourceProjectIsInTheScannedSet [FAIL]
  Assert.Equal() Failure: Collections differ at index 2
  Expected: "ControlServer.Host"
  Actual:   "ControlServer.Experiment"

Failed: 1, Passed: 6
```

这条堵的是最容易无声漏掉的口子：扫描的四个程序集是钉死的，新加一个 `src/` 项目本来会
落在全部断言之外，而绿色的跑分不会告诉任何人。原文见
[`experiment-d-unscanned-source-project.txt`](experiment-d-unscanned-source-project.txt)。

## 全量绿

[`full-suite-green.txt`](full-suite-green.txt)：

```
Passed!  - Failed: 0, Passed: 395, Skipped: 0, Total: 395, Duration: 45 s
```

## 判定为什么做在 IL 上而不是源码文本上

`src/` 下 `.Raw` 有三处文本命中，**全部是注释**（`RouteGraphPorts.cs`、
`HttpRiotRouteCostProbe.cs`、`HttpRouteGraphSource.cs`，三处都在说「不要用它」）。朴素
grep 一上来就是三条假红。反过来，Facade 的调用形态是 `riotSession.Tasks.GetRouteCostAsync(...)`，
而 `session.` 这个前缀在 `src/` 下有六十多行与 RIoT 毫无关系。

读编译产物的成员引用表就没有这两个问题：**编译器发出的成员引用是一次调用，注释不是。**

## 干净 checkout 复验

工作区根 `CLAUDE.md` 的规矩：改动会影响构建的文件后，用干净 checkout 验一次。
本票加了一条 `.gitattributes` 的 `-text` 规则，而那正是「字节规则在拆分时丢失、
导致摘要对不上」那次事故的形状。

临时克隆到别处（`git clone -c core.longpaths=true`，仓库里有既存的超长证据路径），
结果：

```
vendor/8005-agv-program/docs/riot-call-allowlist.md
  dec0bc1046f3f7c969ca53bd332708fe4668ded5370aa308c584305222d7bcee

Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7
```

哈希与钉在测试里的值逐字节一致，`git status` 干净——`-text` 规则在新 checkout 上生效。
