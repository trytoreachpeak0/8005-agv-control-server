# 红证据：业务消息面首次 staged 运行撞上单连接 accept 循环

## 结论

`status = INCONCLUSIVE_RUNNER_ERROR`。**这是 runner 缺陷，不是产品回归。**
绿运行见同级 `evidence/g3/20260830-business-message-fault-injection/`。

保留本目录是为了留下判据本身，不是为了记账。

## 现象

`error.message = Expected a TLS NDJSON response.`

业务代理记录（`business-fault-proxy-events.ndjson`）只有四行：

```
00:00:55.596  proxy-listening   58216 -> 58205
00:01:07.847  connection-opened connectionId=3
00:01:07.848  message           client-to-server SessionHello forwarded
00:01:07.851  connection-closed connectionId=3
```

`SessionHello` 转发出去 4 ms 后连接即断，服务端一个字节都没回。
`business-probe-events.ndjson` 为空（0 字节）——探针在第一次握手就退出了。

## 根因

`OnboardTcpServer.ExecuteAsync` 的 accept 循环是串行的：

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    using TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
    try { await HandleClientAsync(client, stoppingToken).ConfigureAwait(false); }
    ...
}
```

`await HandleClientAsync` 在循环体内，所以服务端同一时刻只服务一个车载连接。
本次运行中真实车载端（`fault-proxy-events.ndjson` 的 connectionId 2）持有会话至
`00:01:07.791`，合成对端在此之前无法被接受。

服务端日志尾部在进程被强杀时随 Serilog 控制台缓冲丢失，因此**没有**从日志倒推原因。

## 判据是怎么单独证出来的

不涉及任何协议知识：持有一条**已完成 TLS 握手、一个字节都不发**的连接，观察第二个客户端。

| 步骤 | 结果 |
| --- | --- |
| 第一条连接握手 | `FIRST_TLS_OK`（保持打开）|
| 第二条连接握手，8 秒超时 | `SECOND_TLS_BLOCKED`：`The operation was canceled.` |
| 关闭第一条后重试 | `THIRD_TLS_OK` |

这条判据独立于本次失败成立，也解释了为什么本机冒烟当初是绿的——那时没有车载端占着连接。

## 修法

排序，不是并发。先停车载端与其代理，等服务端退出 `HandleClientAsync`，再启动业务代理并把
服务端交给业务面。见 `6524a14`。

## 顺带修掉的一项

本目录 `run-result.json` 的 `commits.harnessWorktreeClean = false`，而当时工作树在提交后确实是
干净的。原因是该字段在运行结束时采集，此时仓内的 EvidenceRoot 已作为未跟踪内容存在，导致该字段
恒为假——字段自指。改为在任何证据落盘前采集，并更名为 `harnessWorktreeCleanAtStart`。见 `5456451`。

## 安全性

本次运行同样不动车、不建单、不使用现场凭据；`secretScan` PASS、`secretLeakFiles` 为空、
`temporaryTrustCleanupVerified = true`，临时根证书已在 `finally` 中移除并独立复核为 0。
