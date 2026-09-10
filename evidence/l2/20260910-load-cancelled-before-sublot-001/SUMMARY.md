# L2 场景证据：load-cancelled-before-sublot

结论：**FAIL**

失败原因：Component 'fake-riot' exited with code -532462766 while waiting for: fake RIoT is live.
--- fake-riot stderr, last 20 line(s) ---
   at Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions.CreateDefaultBoundListenSocket(EndPoint endpoint)
   at Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketConnectionListener.Bind()
   at Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportFactory.BindAsync(EndPoint endpoint, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.TransportManager.BindAsync(EndPoint endPoint, ConnectionDelegate connectionDelegate, EndpointConfig endpointConfig, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl.<>c__DisplayClass28_0`1.<<StartAsync>g__OnBind|0>d.MoveNext()
--- End of stack trace from previous location ---
   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.AddressBinder.BindEndpointAsync(ListenOptions endpoint, AddressBindContext context, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions.BindAsync(AddressBindContext context, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.AddressBinder.EndpointsStrategy.BindAsync(AddressBindContext context, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.AddressBinder.BindAsync(ListenOptions[] listenOptions, AddressBindContext context, Func`2 useHttps, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl.BindAsync(CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerImpl.StartAsync[TContext](IHttpApplication`1 application, CancellationToken cancellationToken)
   at Microsoft.AspNetCore.Hosting.GenericWebHostService.StartAsync(CancellationToken cancellationToken)
   at Microsoft.Extensions.Hosting.Internal.Host.<StartAsync>b__15_1(IHostedService service, CancellationToken token)
   at Microsoft.Extensions.Hosting.Internal.Host.ForeachService[T](IEnumerable`1 services, CancellationToken token, Boolean concurrent, Boolean abortOnFirstException, List`1 exceptions, Func`3 operation)
   at Microsoft.Extensions.Hosting.Internal.Host.StartAsync(CancellationToken cancellationToken)
   at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(IHost host, CancellationToken token)
   at Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(IHost host, CancellationToken token)
   at Program.<Main>$(String[] args) in C:\Users\szy\Desktop\8005-workspace\repos\8005-agv-control-server-envelope\tools\ControlServer.FakeRiot\Program.cs:line 8
   at Program.<Main>(String[] args)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T070340568Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `b5a5e0ebb77a54f6cec727ddd90418c2425119fe` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T070340568Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
