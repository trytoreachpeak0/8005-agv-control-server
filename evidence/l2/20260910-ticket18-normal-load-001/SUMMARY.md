# L2 场景证据：normal-load

结论：**FAIL**

失败原因：Component 'fake-onboard' exited with code -532462766 while waiting for: the synthetic peer AGV-L2-001 reached READY.
--- fake-onboard stderr, last 20 line(s) ---
   at System.Net.Sockets.Socket.DoBind(EndPoint endPointSnapshot, SocketAddress socketAddress)
   at System.Net.Sockets.Socket.Bind(EndPoint localEP)
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
   at Program.<Main>$(String[] args) in C:\Users\szy\Desktop\8005-fp\8005-agv-control-server\tools\ControlServer.FakeOnboard\Program.cs:line 8
   at Program.<Main>(String[] args)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T084204353Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `79c8b20f8ed603e2154ec1bdb821cc706308ba1e` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `0.1.1` |
| protocolReleaseIdentity.tag | `protocol-v0.1.1` |
| protocolReleaseIdentity.commit | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` |
| protocolReleaseIdentity.protocolVersion | `1` |
| protocolReleaseIdentity.profileId | `WIRE_TO_GATE_MVP` |
| protocolReleaseIdentity.manifestSha256 | `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f` |
| protocolReleaseIdentity.schemaBundleSha256 | `e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c` |
| protocolReleaseIdentity.vectorsSha256 | `fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T084204353Z` |
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
