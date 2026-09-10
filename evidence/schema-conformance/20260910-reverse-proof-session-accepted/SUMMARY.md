# 反证：删掉一个 required 字段，出站 schema 校验必须变红

- 票：8005-agv-program#33（完成判据「反证」那一条）
- 日期：2026-09-10
- 基线：`8005-agv-control-server` 分支 `feat/outbound-schema-validation`，基于 `683cc23`，未提交的工作树
- 协议身份：`protocol-v0.3.0`（manifest `b6c81ca9…`，schema bundle `68bfd531…`）
- 结论：**FAIL，而且红得对**

## 做了什么

临时把 `OnboardMessageProcessor` 发 `SessionAccepted` 时 payload 里的 `serverInstanceId` 删掉（schema 里它是 `required`），重新构建，只跑 `W2G-IS-00` 这一刀：

```
WIRE_TO_GATE_SCHEMA_REPORT_DIR=<本目录> dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter "IntegrationSlice=W2G-IS-00"
```

跑完即还原。

## 看到了什么

```
[Test Assembly Cleanup Failure (...ControlServer.Tests.dll)] Xunit.Sdk.TestPipelineException
Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56
```

退出码 **1**。56 个测试全部通过——这正是 #12 的形状：切片里没有任何一条断言读 `serverInstanceId`，缺了也照样绿。校验器的报告（`schema-conformance.txt`）：

```
SCHEMA VIOLATION x5: SessionAccepted sent by product OnboardMessageProcessor.SerializeEnvelope <- OnboardMessageProcessor.<ProcessAsync <- OnboardMessageProcessor.ProcessAsync(lambda)
  #/payload [required] Validation properties - the required property 'serverInstanceId' was not present.
```

报错指到了发送方法 `OnboardMessageProcessor.SerializeEnvelope`，并注明是产品出站、共 5 条。

## 顺带发现并已修正

产地名第二段 `<ProcessAsync` 是反混淆的瑕疵：async lambda 的状态机类型名是嵌套的 `<<ProcessAsync>b__0>d`，旧写法只剥一层 `<`。已改为剥掉所有前导 `<`，lambda 标 `(lambda)`、本地函数标出函数名。本目录保留的是修正前的原始输出。

## 为什么先前那次反证没红

第一次选的是 `HeartbeatAck`（删 `serverTime`），同样只跑 `W2G-IS-00`，结果退出码 0。原因不在校验器：**全量 422 个测试一条 `HeartbeatAck` 都没发出**。全量覆盖账里产品方向 31 种报文有 10 种从未被任何测试产出：`HeartbeatAck`、`SessionRejected`、`ProtocolProblem`、`CapabilitySnapshotRequested`、`SafetyStateSnapshotRequested`、`ExceptionRecoverySessionRejected`、`HardwareRecoveryRecordResult`、`LoadCompensationRejected`、`LoadCorrectionRejected`、`ManualChargingReturnToServiceResult`。这十种的发送方法缺字段，这道校验看不见——覆盖账只报告不判死，是票面定的。
