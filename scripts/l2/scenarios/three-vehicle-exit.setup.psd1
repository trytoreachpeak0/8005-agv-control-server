# 轨 B 的出口场景：三台车同时端到端。
#
# `Fleet` 列的是**主车之外**的车。编排器把主对（AGV-L2-001/BROKERX-L2-0001）放在第一位，
# 因为 `JourneyRuntimeOptions` 的校验器要求名册包含主对——让每个 setup 文件自己重写一遍主对，
# 就是给它一个写错的机会。
#
# 车载端不用再列一遍：`OnboardPeers` 缺席时编排器按 `Fleet` 逐车派生，一台车一个进程一个控制面。
# 名册里的车与它连出去的那条会话是同一台车，两份名单是它们开始互相不一致的方式。
#
# 路网引擎开着。轨 B 的出口要求引擎在派车链路里真的工作，而不是被绕过——三台车全部走通，
# 说明可达性判据对三台车各判过一次并且都放行了。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' },
        @{ AgvId = 'AGV-L2-003'; VehicleKey = 'BROKERX-L2-0003' }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
