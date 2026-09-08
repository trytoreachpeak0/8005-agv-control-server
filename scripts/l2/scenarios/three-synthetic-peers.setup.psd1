# 三个合成车载端，各自一个 AgvId、各自一个进程。
#
# 进程隔离不是省事，是这条场景要证的那件事本身：会话不串如果靠一个进程里的三份状态互相不
# 踩，那证的是那份代码记得分开；三个进程各连各的，不串是结构上的。
#
# 三台都等 READY。票 09 之前后两台写着 WaitForReady = $false，因为 ControlServer 的 accept
# 循环是串行的，处理完一个连接才接下一个，同一时刻只有一台车的会话是活的。票 09 把 accept
# 改成并发、把 OnboardPeer 改成按 AgvId 持有 N 条连接之后，三台车能同时到 READY，这里就不
# 该再有例外。
#
# 本场景证的仍然只是会话这一层：三个进程、三个控制面、三个 AgvId，服务端同时持有三条会话。
# 三车端到端（派单、装货、走完 journey）是票 18 的出口。
@{
    OnboardPeers = @(
        @{ AgvId = 'AGV-FAKE-001' },
        @{ AgvId = 'AGV-FAKE-002' },
        @{ AgvId = 'AGV-FAKE-003' }
    )
}
