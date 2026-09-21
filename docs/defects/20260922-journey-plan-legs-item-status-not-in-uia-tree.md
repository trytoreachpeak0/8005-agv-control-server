# 缺陷：车载端计划腿列表每行的 ItemStatus 不在 UIA 树上，服务端 G3 读不到

Status: 已登记，本票（control-server#218）绕开：计划腿行序改读每行第一个 TextBlock（序位）。车载端要不要改由调度决定，改的话单开车载端票
Owner repository: `8005-agv-onboard-hmi`（`src/SQCD.Agv.Wpf/MainWindow.xaml` 的 `JourneyPlanLegs` 模板，onboard-hmi#134、PR #143）
Found by: control-server#218 开工时的 UIA 实测，2026-09-22，[`evidence/b7-15/uia-probe/output.txt`](../../evidence/b7-15/uia-probe/output.txt)（脚本同目录）
Product at discovery: onboard-hmi `deeba94c`（`w2g/fp-v2-impl` 顶端）

## 现象

onboard-hmi#134 在 control-server#218 评论里给出的契约：计划腿列表 `JourneyPlanLegs`「每行 Grid 带 ItemStatus」，值为
`sequence|stopPurposeCategory|state`，供服务端 G3 场景读顺序与状态。XAML 是这样写的：

```xml
<ItemsControl AutomationProperties.AutomationId="JourneyPlanLegs" ItemsSource="{Binding JourneyPlanLegs}">
  <ItemsControl.ItemTemplate>
    <DataTemplate DataType="{x:Type vm:JourneyPlanLegRow}">
      <Grid Margin="0,1" AutomationProperties.ItemStatus="{Binding ItemStatus}"> ...
```

用同形的 XAML（`ItemsControl` + 模板根是带 `ItemStatus` 的 `Grid`，另有一个 `ListBox` 模板里带 `ItemStatus` 的 `TextBlock` 作对照）
起一个窗口，另一个进程经 UI Automation 走树（ControlView 与 RawView 都走了），结果：

```
== JourneyPlanLegs (raw view)
  ControlType.DataItem id='' name='@{Seq=1; Station=N1-3; Status=1|PICKUP|ACTIVE}' status='' class='ItemsControlItem'
    ControlType.Text id='' name='1' status='' class='TextBlock'
    ControlType.Text id='' name='N1-3 x' status='' class='TextBlock'
```

`Grid` 不在树上，每行由 `ItemsControl` 给的 `DataItem` 代表，它的 `ItemStatus` 是空的。对照组里 `WorklistItemSide`（一个 `TextBlock`）的
`ItemStatus` 读得到（`status='FRONT'`）。

## 原因

WPF 的 `Panel`（`Grid` 是其一）没有自动化 peer，不进 UI Automation 树；挂在它上面的 `AutomationProperties.ItemStatus` 于是没有载体。
`ItemsControl` 的项 peer（`ItemsControlItemAutomationPeer`）取 `ItemStatus` 时找的是项容器（`ContentPresenter`），也不是模板里的 `Grid`。
车载端的 `scripts/check-ui-layout.ps1` 只按正则检查 XAML 里写了这个属性，G2 读的是视图模型的 `JourneyPlanLegRow.ItemStatus`，
都不经过 UIA，所以两道检查都看不见这件事。

## 影响

- 服务端 G3 场景 `g3-multi-stop-plan` 读不到契约里的那个值。它改读每行第一个 `TextBlock`（`JourneyPlanLegRow.SequenceText`）拿行序，
  这足以判 `DISPLAY_FULL_JOURNEY_PLAN`（行数）与 `NEVER_REORDER_LEGS_LOCALLY`（行序），不足以按原始码判每条腿的
  `stopPurposeCategory` 与 `state`——那两样界面上只有中文文案（`StopPurposeText`、`StateText`）。
- `DataItem` 的 `Name` 是行对象的 `ToString()`；车载端的行是 C# `record`，`ToString()` 会带上 `ItemStatus = …`，所以
  `L2MultiStopJourney.psm1` 顺带从 `Name` 里取它记进日志。**那只是 record 的打印格式，不是契约**，判据不依赖它。

## 怎样修（给车载端，不在本票做）

把 `ItemStatus` 挂到一个有 peer 的元素上，例如行里显示序位的那个 `TextBlock`，并给它一个 `AutomationId`（如 `JourneyPlanLegRow`），
与 `WorklistItemSide` 同一种写法；`check-ui-layout.ps1` 的 `visibleJourneyPlanLegs` 相应改检查项。修好之后服务端场景可以改回按
`ItemStatus` 判用途类别与状态。
