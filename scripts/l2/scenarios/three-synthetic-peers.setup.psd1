# 三个合成车载端，各自一个 AgvId、各自一个进程。
#
# 进程隔离不是省事，是这条场景要证的那件事本身：会话不串如果靠一个进程里的三份状态互相不
# 踩，那证的是那份代码记得分开；三个进程各连各的，不串是结构上的。
#
# 后两台 WaitForReady = $false，因为服务端现在到不了那一步：ControlServer 的 accept 循环是
# 串行的（OnboardTcpServer.cs 里 await HandleClientAsync 在循环内），处理完一个连接才接下一
# 个，所以同一时刻只有一台车的会话是活的，其余排队。那是票 09 第 1 项要换掉的单车传输层。
#
# 本场景因此只证合成对端这一侧：三个进程、三个控制面、三个 AgvId，互不冒充。服务端能同时
# 持有三条会话是票 09 的出口，三车端到端是票 18 的出口。
@{
    OnboardPeers = @(
        @{ AgvId = 'AGV-FAKE-001' },
        @{ AgvId = 'AGV-FAKE-002'; WaitForReady = $false },
        @{ AgvId = 'AGV-FAKE-003'; WaitForReady = $false }
    )
}
