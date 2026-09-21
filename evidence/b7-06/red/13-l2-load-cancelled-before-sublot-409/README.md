# `load-cancelled-before-sublot` 在 CI 全量并行里红了一次（run 35544281388），定性未完成

**这一份是待定项，不是结论。**留它是因为它红过，而红过的运行不许被一次绿盖掉。

## 现象

`l2` run [35544281388](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35544281388)
在 `dbd63230`（第 4 批 + merge `74a255e763`）上失败，唯一红的场景是 `load-cancelled-before-sublot`，
exit code 1，21 秒。

**八条断言全部 `PASS`。**场景级 `outcome` 是 `FAIL`，`failureReason` 是：

```
响应状态代码未指示成功: 409 (Conflict)。
```

`assertions.json` 里只有 `L2-CB-01` 到 `L2-CB-03` 共 8 条，而场景脚本一直写到 `L2-CB-08`——
**场景在 CB-03 之后中断**，后面的断言从未执行。CB-03 之后紧接着的是 `Wait-PickupIntent`
与 B2 段的断联／重连。

服务端日志里**没有**真正的 409（`control-server.out.log` 里 `409` 的命中全部来自端口号 `47409`
与 `CreateGateAudit` 的 `ConflictDetail` 列名）。那个 409 因此来自哪一次 HTTP 调用，尚未定位。

## 已经排除的：不是本票的产品改动

上一轮 CI（`35540894914`，`85353c8f`，第 2 批末）**l2 success**；协调者在 `fp/v2-impl` 顶端
`74a255e763` 跑的兜底**l2 也 success**。所以差集是第 3 批与第 4 批。

- **第 3 批零产品代码**（只改注释、`l2.yml` 注释、证据）。
- **第 4 批唯一的产品改动**是 `WireToGateStore` 里新增的「当前下一站序位没变」检查。
  `ApplyResequencingAsync` 全仓**只有一个调用点**（`WireToGateStore.cs:714`，在
  `StageAndCommitAppendAsync` 里，参数是 `JourneyAppendPlan`）——**只在追加路径**。
  而 `load-cancelled-before-sublot` 不追加：它的两单都是新受理的旅程，走 `AcceptAsync`。

这条排除是按调用点实读的，不是按「我觉得它只在追加路径」。

## 没有排除的：并行下的确定性红

- **本机单跑 PASS**（当前树）。
- **CI 单跑 PASS**：run [35551326586](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/35551326586)，
  `-f scenarios=load-cancelled-before-sublot`，在 `ee500c0a` 上（比红的那棵树多两个提交，
  产品代码只多注释，实质同树）。

**这两次绿都没有排除「并行下确定性红」**：CI 全量把场景分到 lane／slot 并行跑，单跑没有那个压力。
一红两绿里，两次绿的条件与红的那次不同，所以它们不是对照，只是说明它不是「任何条件下都红」。

样本现在是**一次红**，不足以判偶发，也不足以判确定性。

## 下一个数据点

merge `deb6de1f` 之后要跑的那一轮全量 CI 就是第二个样本，条件与红的那次相同（全量并行）。

- 那一轮**绿** → 两次全量一红一绿，按 [`l2-flake-needs-deterministic-l1-probe`] 的口径仍不足以宣告无事，
  但可以转成「已知偶发」记录，并在出口前用确定性 L1 用例判一次；
- 那一轮**红在同一个场景** → 是确定性的，必须定位那次 409 的调用点，单开票。

无论哪种，`Wait-PickupIntent` 与 B2 断联重连那一段是查 409 的起点，而那一段与本票的改动无关。
